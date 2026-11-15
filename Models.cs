using MongoDB.Bson.Serialization.Attributes;

namespace LibraryNoSql;

// Один документ MongoDB = один відгук разом з тегами та коментарями.
// У PostgreSQL ця сама модель розбита на таблиці reviews, review_tags, review_comments.

public class ReviewComment
{
    public int MemberId { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class Review
{
    [BsonId] public long Id { get; set; }
    public int BookId { get; set; }
    public int MemberId { get; set; }
    public int Rating { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public int ReadingProgress { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<ReviewComment> Comments { get; set; } = new();
}

public static class DataGenerator
{
    public static readonly string[] AllTags =
    {
        "fantasy", "classic", "history", "poetry", "detective", "romance",
        "science", "adventure", "drama", "humor", "biography", "children"
    };

    private static readonly string[] Words =
    {
        "книга", "сюжет", "автор", "герої", "цікава", "нудна", "рекомендую", "персонажі",
        "мова", "фінал", "початок", "емоції", "історія", "стиль", "глибока", "легка",
        "складна", "атмосфера", "переклад", "видання"
    };

    private static readonly DateTime Start = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Фіксований seed: обидві бази отримують абсолютно однакові дані
    public static List<Review> Generate(int count, long startId,
        IReadOnlyList<int> bookIds, IReadOnlyList<int> memberIds, int seed)
    {
        var rnd = new Random(seed);
        var list = new List<Review>(count);

        for (int i = 0; i < count; i++)
        {
            var created = Start.AddMinutes(rnd.Next(0, 700_000));
            var review = new Review
            {
                Id = startId + i,
                BookId = bookIds[rnd.Next(bookIds.Count)],
                MemberId = memberIds[rnd.Next(memberIds.Count)],
                Rating = rnd.Next(1, 6),
                Title = Sentence(rnd, 4),
                Body = Sentence(rnd, 25),
                ReadingProgress = rnd.Next(10, 101),
                CreatedAt = created,
                Tags = AllTags.OrderBy(_ => rnd.Next()).Take(rnd.Next(1, 5)).ToList()
            };

            int commentCount = rnd.Next(0, 5);
            for (int c = 0; c < commentCount; c++)
            {
                review.Comments.Add(new ReviewComment
                {
                    MemberId = memberIds[rnd.Next(memberIds.Count)],
                    Text = Sentence(rnd, 10),
                    CreatedAt = created.AddMinutes(rnd.Next(1, 10_000))
                });
            }

            list.Add(review);
        }

        return list;
    }

    private static string Sentence(Random rnd, int words) =>
        string.Join(' ', Enumerable.Range(0, words).Select(_ => Words[rnd.Next(Words.Length)]));
}
