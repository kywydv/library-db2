using System.Diagnostics;

namespace LibraryNoSql;

public static class Benchmark
{
    private const int BulkCount = 100_000;

    private sealed record Test(string Name, int Ops, int Repeat,
        Func<IReviewStore, Task> Body, Func<IReviewStore, Task>? Setup = null);

    public static async Task RunAsync(PgStore pg, MongoStore mongo)
    {
        var (books, members) = await pg.LoadIdsAsync();
        if (books.Count == 0 || members.Count == 0)
        {
            Console.WriteLine("У PostgreSQL немає книг або читачів. Виконайте lab2_sql.sql.");
            return;
        }

        var stores = new IReviewStore[] { pg, mongo };

        Console.WriteLine($"Генерую {BulkCount} відгуків...");
        var bulk = DataGenerator.Generate(BulkCount, 1, books, members, seed: 1);
        var singles = DataGenerator.Generate(1000, BulkCount + 1, books, members, seed: 2);

        var rnd = new Random(3);
        var bookSample = Enumerable.Range(0, 300).Select(_ => books[rnd.Next(books.Count)]).ToList();
        var reviewSample = Enumerable.Range(0, 1000).Select(_ => (long)rnd.Next(1, BulkCount + 1)).ToList();

        var tests = new List<Test>
        {
            new("1. Масова вставка 100000 відгуків", BulkCount, 1,
                s => s.BulkInsertAsync(bulk), s => s.ClearAsync()),

            new("2. Додавання відгуку (по одному)", singles.Count, 1, async s =>
            {
                foreach (var r in singles) await s.InsertOneAsync(r);
            }),

            new("3. Відгуки книги (20 шт з тегами і коментарями)", bookSample.Count, 3, async s =>
            {
                foreach (var id in bookSample) await s.GetByBookAsync(id, 20);
            }),

            new("4. Топ-10 книг за середньою оцінкою", 20, 3, async s =>
            {
                for (int i = 0; i < 20; i++) await s.TopBooksAsync();
            }),

            new("5. Останні 5-зіркові відгуки за тегом", 200, 3, async s =>
            {
                for (int i = 0; i < 200; i++)
                    await s.LatestFiveStarByTagAsync(DataGenerator.AllTags[i % DataGenerator.AllTags.Length], 20);
            }),

            new("6. Додавання коментаря до відгуку", reviewSample.Count, 1, async s =>
            {
                foreach (var id in reviewSample)
                    await s.AddCommentAsync(id, new ReviewComment
                    {
                        MemberId = members[0],
                        Text = "Дякую за відгук",
                        CreatedAt = DateTime.UtcNow
                    });
            })
        };

        Console.WriteLine();
        Console.WriteLine($"{"Тест",-52} {"PostgreSQL, мс",16} {"MongoDB, мс",14} {"PG мс/оп",10} {"Mongo мс/оп",12}");
        Console.WriteLine(new string('-', 108));

        foreach (var t in tests)
        {
            var ms = new double[2];
            for (int i = 0; i < 2; i++)
            {
                var s = stores[i];
                if (t.Setup != null) await t.Setup(s);
                if (t.Repeat > 1) await t.Body(s); // прогрів, не враховується

                var times = new List<double>();
                for (int k = 0; k < t.Repeat; k++)
                {
                    var sw = Stopwatch.StartNew();
                    await t.Body(s);
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                ms[i] = times[times.Count / 2]; // медіана
            }

            Console.WriteLine(
                $"{t.Name,-52} {ms[0],16:F1} {ms[1],14:F1} {ms[0] / t.Ops,10:F3} {ms[1] / t.Ops,12:F3}");

            if (t.Setup != null)
                Console.WriteLine($"   (рядків/документів: PostgreSQL {await pg.CountAsync()}, MongoDB {await mongo.CountAsync()})");
        }

        Console.WriteLine();
        Console.WriteLine("Приклад документа в MongoDB:");
        Console.WriteLine(await mongo.GetSampleJsonAsync());
    }
}
