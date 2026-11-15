using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using NpgsqlTypes;

namespace LibraryNoSql;

public interface IReviewStore
{
    string Name { get; }
    Task ClearAsync();
    Task BulkInsertAsync(List<Review> items);
    Task InsertOneAsync(Review review);
    Task<long> CountAsync();
    Task<List<Review>> GetByBookAsync(int bookId, int limit);
    Task<List<(int BookId, double Avg, int Count)>> TopBooksAsync();
    Task<List<long>> LatestFiveStarByTagAsync(string tag, int limit);
    Task AddCommentAsync(long reviewId, ReviewComment comment);
}

// =====================================================================
// PostgreSQL: відгук розбитий на 3 таблиці
// =====================================================================
public class PgStore : IReviewStore
{
    private readonly NpgsqlDataSource _ds;
    public string Name => "PostgreSQL";

    public PgStore(string connectionString) => _ds = NpgsqlDataSource.Create(connectionString);

    public async Task<(List<int> Books, List<int> Members)> LoadIdsAsync()
    {
        await using var conn = await _ds.OpenConnectionAsync();
        var books = new List<int>();
        var members = new List<int>();

        await using (var cmd = new NpgsqlCommand("SELECT id FROM books WHERE is_deleted = FALSE ORDER BY id", conn))
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync()) books.Add(rd.GetInt32(0));

        await using (var cmd = new NpgsqlCommand("SELECT id FROM members WHERE is_deleted = FALSE ORDER BY id", conn))
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync()) members.Add(rd.GetInt32(0));

        return (books, members);
    }

    public async Task ClearAsync()
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "TRUNCATE review_comments, review_tags, reviews RESTART IDENTITY", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task BulkInsertAsync(List<Review> items)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var w = await conn.BeginBinaryImportAsync(
            "COPY reviews (id, book_id, member_id, rating, title, body, reading_progress, created_at) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var r in items)
            {
                await w.StartRowAsync();
                await w.WriteAsync(r.Id, NpgsqlDbType.Bigint);
                await w.WriteAsync(r.BookId, NpgsqlDbType.Integer);
                await w.WriteAsync(r.MemberId, NpgsqlDbType.Integer);
                await w.WriteAsync((short)r.Rating, NpgsqlDbType.Smallint);
                await w.WriteAsync(r.Title, NpgsqlDbType.Varchar);
                await w.WriteAsync(r.Body, NpgsqlDbType.Text);
                await w.WriteAsync((short)r.ReadingProgress, NpgsqlDbType.Smallint);
                await w.WriteAsync(r.CreatedAt, NpgsqlDbType.TimestampTz);
            }
            await w.CompleteAsync();
        }

        await using (var w = await conn.BeginBinaryImportAsync(
            "COPY review_tags (review_id, tag) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var r in items)
                foreach (var tag in r.Tags)
                {
                    await w.StartRowAsync();
                    await w.WriteAsync(r.Id, NpgsqlDbType.Bigint);
                    await w.WriteAsync(tag, NpgsqlDbType.Varchar);
                }
            await w.CompleteAsync();
        }

        await using (var w = await conn.BeginBinaryImportAsync(
            "COPY review_comments (review_id, member_id, text, created_at) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var r in items)
                foreach (var c in r.Comments)
                {
                    await w.StartRowAsync();
                    await w.WriteAsync(r.Id, NpgsqlDbType.Bigint);
                    await w.WriteAsync(c.MemberId, NpgsqlDbType.Integer);
                    await w.WriteAsync(c.Text, NpgsqlDbType.Text);
                    await w.WriteAsync(c.CreatedAt, NpgsqlDbType.TimestampTz);
                }
            await w.CompleteAsync();
        }

        await tx.CommitAsync();
    }

    public async Task InsertOneAsync(Review r)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var cmd = new NpgsqlCommand(
            @"INSERT INTO reviews (id, book_id, member_id, rating, title, body, reading_progress, created_at)
              VALUES (@id, @book, @member, @rating, @title, @body, @progress, @created)", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", r.Id);
            cmd.Parameters.AddWithValue("book", r.BookId);
            cmd.Parameters.AddWithValue("member", r.MemberId);
            cmd.Parameters.AddWithValue("rating", (short)r.Rating);
            cmd.Parameters.AddWithValue("title", r.Title);
            cmd.Parameters.AddWithValue("body", r.Body);
            cmd.Parameters.AddWithValue("progress", (short)r.ReadingProgress);
            cmd.Parameters.AddWithValue("created", r.CreatedAt);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO review_tags (review_id, tag) SELECT @id, unnest(@tags)", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", r.Id);
            cmd.Parameters.AddWithValue("tags", r.Tags.ToArray());
            await cmd.ExecuteNonQueryAsync();
        }

        if (r.Comments.Count > 0)
        {
            await using var cmd = new NpgsqlCommand(
                @"INSERT INTO review_comments (review_id, member_id, text, created_at)
                  SELECT @id, m, t, d FROM unnest(@members, @texts, @dates) AS x(m, t, d)", conn, tx);
            cmd.Parameters.AddWithValue("id", r.Id);
            cmd.Parameters.AddWithValue("members", r.Comments.Select(c => c.MemberId).ToArray());
            cmd.Parameters.AddWithValue("texts", r.Comments.Select(c => c.Text).ToArray());
            cmd.Parameters.AddWithValue("dates", r.Comments.Select(c => c.CreatedAt).ToArray());
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    public async Task<long> CountAsync()
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM reviews", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // Відгуки книги разом з тегами та коментарями: 3 запити (reviews, tags, comments)
    public async Task<List<Review>> GetByBookAsync(int bookId, int limit)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        var list = new List<Review>();

        await using (var cmd = new NpgsqlCommand(
            @"SELECT id, book_id, member_id, rating, title, body, reading_progress, created_at
              FROM reviews WHERE book_id = @b ORDER BY created_at DESC LIMIT @l", conn))
        {
            cmd.Parameters.AddWithValue("b", bookId);
            cmd.Parameters.AddWithValue("l", limit);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                list.Add(new Review
                {
                    Id = rd.GetInt64(0),
                    BookId = rd.GetInt32(1),
                    MemberId = rd.GetInt32(2),
                    Rating = rd.GetInt16(3),
                    Title = rd.GetString(4),
                    Body = rd.GetString(5),
                    ReadingProgress = rd.GetInt16(6),
                    CreatedAt = rd.GetDateTime(7)
                });
            }
        }

        if (list.Count == 0) return list;
        var ids = list.Select(r => r.Id).ToArray();
        var byId = list.ToDictionary(r => r.Id);

        await using (var cmd = new NpgsqlCommand(
            "SELECT review_id, tag FROM review_tags WHERE review_id = ANY(@ids)", conn))
        {
            cmd.Parameters.AddWithValue("ids", ids);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                byId[rd.GetInt64(0)].Tags.Add(rd.GetString(1));
        }

        await using (var cmd = new NpgsqlCommand(
            @"SELECT review_id, member_id, text, created_at FROM review_comments
              WHERE review_id = ANY(@ids) ORDER BY id", conn))
        {
            cmd.Parameters.AddWithValue("ids", ids);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                byId[rd.GetInt64(0)].Comments.Add(new ReviewComment
                {
                    MemberId = rd.GetInt32(1),
                    Text = rd.GetString(2),
                    CreatedAt = rd.GetDateTime(3)
                });
        }

        return list;
    }

    public async Task<List<(int BookId, double Avg, int Count)>> TopBooksAsync()
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT book_id, avg(rating)::float8 AS avg_rating, count(*)::int AS cnt
              FROM reviews GROUP BY book_id HAVING count(*) >= 50
              ORDER BY avg_rating DESC LIMIT 10", conn);
        var res = new List<(int, double, int)>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            res.Add((rd.GetInt32(0), rd.GetDouble(1), rd.GetInt32(2)));
        return res;
    }

    public async Task<List<long>> LatestFiveStarByTagAsync(string tag, int limit)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT r.id FROM reviews r
              JOIN review_tags t ON t.review_id = r.id
              WHERE t.tag = @t AND r.rating = 5
              ORDER BY r.created_at DESC LIMIT @l", conn);
        cmd.Parameters.AddWithValue("t", tag);
        cmd.Parameters.AddWithValue("l", limit);
        var res = new List<long>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) res.Add(rd.GetInt64(0));
        return res;
    }

    public async Task AddCommentAsync(long reviewId, ReviewComment c)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO review_comments (review_id, member_id, text, created_at)
              VALUES (@r, @m, @t, @d)", conn);
        cmd.Parameters.AddWithValue("r", reviewId);
        cmd.Parameters.AddWithValue("m", c.MemberId);
        cmd.Parameters.AddWithValue("t", c.Text);
        cmd.Parameters.AddWithValue("d", c.CreatedAt);
        await cmd.ExecuteNonQueryAsync();
    }
}

// =====================================================================
// MongoDB: відгук = один документ (теги та коментарі вкладені)
// =====================================================================
public class MongoStore : IReviewStore
{
    private readonly IMongoDatabase _db;
    private IMongoCollection<Review> _col;
    public string Name => "MongoDB";

    public MongoStore(string connectionString)
    {
        var client = new MongoClient(connectionString);
        _db = client.GetDatabase("library_nosql");
        _col = _db.GetCollection<Review>("reviews");
    }

    public async Task ClearAsync()
    {
        await _db.DropCollectionAsync("reviews");
        _col = _db.GetCollection<Review>("reviews");
        await _col.Indexes.CreateManyAsync(new[]
        {
            // відгуки книги, найновіші першими
            new CreateIndexModel<Review>(Builders<Review>.IndexKeys
                .Ascending(x => x.BookId).Descending(x => x.CreatedAt)),
            // пошук за тегом та оцінкою, найновіші першими (multikey-індекс по масиву Tags)
            new CreateIndexModel<Review>(Builders<Review>.IndexKeys
                .Ascending(x => x.Tags).Ascending(x => x.Rating).Descending(x => x.CreatedAt))
        });
    }

    public async Task BulkInsertAsync(List<Review> items)
    {
        foreach (var chunk in items.Chunk(5000))
            await _col.InsertManyAsync(chunk, new InsertManyOptions { IsOrdered = false });
    }

    public Task InsertOneAsync(Review review) => _col.InsertOneAsync(review);

    public Task<long> CountAsync() => _col.CountDocumentsAsync(FilterDefinition<Review>.Empty);

    public Task<List<Review>> GetByBookAsync(int bookId, int limit) =>
        _col.Find(x => x.BookId == bookId).SortByDescending(x => x.CreatedAt).Limit(limit).ToListAsync();

    public async Task<List<(int BookId, double Avg, int Count)>> TopBooksAsync()
    {
        var pipeline = new[]
        {
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", "$BookId" },
                { "avg", new BsonDocument("$avg", "$Rating") },
                { "cnt", new BsonDocument("$sum", 1) }
            }),
            new BsonDocument("$match", new BsonDocument("cnt", new BsonDocument("$gte", 50))),
            new BsonDocument("$sort", new BsonDocument("avg", -1)),
            new BsonDocument("$limit", 10)
        };

        var docs = await _col.Aggregate<BsonDocument>(pipeline).ToListAsync();
        return docs.Select(d => (d["_id"].ToInt32(), d["avg"].ToDouble(), d["cnt"].ToInt32())).ToList();
    }

    public Task<List<long>> LatestFiveStarByTagAsync(string tag, int limit) =>
        _col.Find(x => x.Tags.Contains(tag) && x.Rating == 5)
            .SortByDescending(x => x.CreatedAt)
            .Limit(limit)
            .Project(x => x.Id)
            .ToListAsync();

    public Task AddCommentAsync(long reviewId, ReviewComment c) =>
        _col.UpdateOneAsync(x => x.Id == reviewId, Builders<Review>.Update.Push(x => x.Comments, c));

    public async Task<string> GetSampleJsonAsync()
    {
        var doc = await _col.Find(FilterDefinition<Review>.Empty).FirstOrDefaultAsync();
        return doc == null
            ? "(колекція порожня)"
            : doc.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { Indent = true });
    }
}
