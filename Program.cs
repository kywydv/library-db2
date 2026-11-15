using System.Text;
using LibraryNoSql;

// !!! Змініть пароль PostgreSQL на свій
const string PgConnection = "Host=localhost;Port=5432;Database=library;Username=postgres;Password=1234";
const string MongoConnection = "mongodb://localhost:27017";
const string RedisConnection = "localhost:6379";

Console.OutputEncoding = Encoding.UTF8;

var pg = new PgStore(PgConnection);
var mongo = new MongoStore(MongoConnection);

while (true)
{
    Console.WriteLine();
    Console.WriteLine("1 - Тест швидкодії: PostgreSQL проти MongoDB");
    Console.WriteLine("2 - Redis: базові запити");
    Console.WriteLine("0 - Вихід");
    Console.Write("Ваш вибір: ");

    var choice = Console.ReadLine()?.Trim();
    try
    {
        switch (choice)
        {
            case "1": await Benchmark.RunAsync(pg, mongo); break;
            case "2": await RedisDemo.RunAsync(PgConnection, RedisConnection); break;
            case "0": return;
            default: Console.WriteLine("Невідомий пункт"); break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Помилка: {ex.Message}");
    }
}
