using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using StackExchange.Redis;

namespace LibraryNoSql;

// Redis (key-value): кеш каталогу, лічильники переглядів, рейтинг популярних книг,
// профіль читача (hash) і список останніх пошуків (list).
public static class RedisDemo
{
    public static async Task RunAsync(string pgConnectionString, string redisConnectionString)
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
        var db = mux.GetDatabase();
        await using var pg = NpgsqlDataSource.Create(pgConnectionString);

        var bookIds = new List<int>();
        await using (var conn = await pg.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand("SELECT id FROM books WHERE is_deleted = FALSE ORDER BY id LIMIT 50", conn))
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync()) bookIds.Add(rd.GetInt32(0));

        if (bookIds.Count == 0)
        {
            Console.WriteLine("У PostgreSQL немає книг. Виконайте library_db.sql.");
            return;
        }

        // ---------- 1. Кеш (cache-aside): GET / SET з TTL ----------
        Console.WriteLine("== 1. Кеш картки книги (SET key value EX 60 / GET key) ==");
        int bookId = bookIds[0];
        await db.KeyDeleteAsync($"book:{bookId}");

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var sw = Stopwatch.StartNew();
            string? json = await db.StringGetAsync($"book:{bookId}");
            string source;

            if (json == null)
            {
                source = "MISS, читаю з PostgreSQL (v_book_catalog)";
                await using var conn = await pg.OpenConnectionAsync();
                await using var cmd = new NpgsqlCommand(
                    "SELECT title, authors, available_copies FROM v_book_catalog WHERE id = @id", conn);
                cmd.Parameters.AddWithValue("id", bookId);
                await using var rd = await cmd.ExecuteReaderAsync();
                json = await rd.ReadAsync()
                    ? JsonSerializer.Serialize(new { title = rd.GetString(0), authors = rd.GetString(1), available = rd.GetInt32(2) })
                    : "{}";
                await db.StringSetAsync($"book:{bookId}", json, TimeSpan.FromSeconds(60));
            }
            else
            {
                source = "HIT, взято з Redis";
            }

            sw.Stop();
            Console.WriteLine($"Спроба {attempt}: {source}, {sw.Elapsed.TotalMilliseconds:F2} мс -> {json}");
        }
        Console.WriteLine($"TTL ключа: {(await db.KeyTimeToLiveAsync($"book:{bookId}"))?.TotalSeconds:F0} с");

        // ---------- 2. Лічильник (INCR) ----------
        Console.WriteLine("\n== 2. Лічильник переглядів (INCR views:book:ID) ==");
        for (int i = 0; i < 3; i++)
            Console.WriteLine($"views:book:{bookId} = {await db.StringIncrementAsync($"views:book:{bookId}")}");

        // ---------- 3. Sorted set: рейтинг популярних книг ----------
        Console.WriteLine("\n== 3. Топ книг (ZINCRBY top:books 1 ID / ZREVRANGE top:books 0 4 WITHSCORES) ==");
        await db.KeyDeleteAsync("top:books");
        var rnd = new Random(5);
        var batch = db.CreateBatch();
        var tasks = new List<Task>();
        for (int i = 0; i < 5000; i++)
        {
            // перші книги переглядають частіше
            int idx = (int)(Math.Pow(rnd.NextDouble(), 2) * bookIds.Count);
            tasks.Add(batch.SortedSetIncrementAsync("top:books", bookIds[idx], 1));
        }
        batch.Execute();
        await Task.WhenAll(tasks);

        foreach (var e in await db.SortedSetRangeByRankWithScoresAsync("top:books", 0, 4, Order.Descending))
            Console.WriteLine($"книга {e.Element}: {e.Score} переглядів");

        // ---------- 4. Hash: профіль читача ----------
        Console.WriteLine("\n== 4. Профіль читача (HSET / HGETALL / EXPIRE) ==");
        await db.HashSetAsync("member:1", new[]
        {
            new HashEntry("name", "Іван Петренко"),
            new HashEntry("card", "CARD-1"),
            new HashEntry("plan", "Стандарт")
        });
        await db.KeyExpireAsync("member:1", TimeSpan.FromMinutes(30));
        foreach (var h in await db.HashGetAllAsync("member:1"))
            Console.WriteLine($"{h.Name}: {h.Value}");

        // ---------- 5. List: останні пошуки ----------
        Console.WriteLine("\n== 5. Останні пошуки (LPUSH / LTRIM 0 4 / LRANGE 0 -1) ==");
        await db.KeyDeleteAsync("recent:searches:1");
        foreach (var q in new[] { "Маруся Чурай", "Костенко", "поезія", "історія України", "детектив", "фентезі" })
            await db.ListLeftPushAsync("recent:searches:1", q);
        await db.ListTrimAsync("recent:searches:1", 0, 4);
        foreach (var v in await db.ListRangeAsync("recent:searches:1", 0, -1))
            Console.WriteLine(v);
    }
}
