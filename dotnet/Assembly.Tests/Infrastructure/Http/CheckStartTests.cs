using Assembly.Desktop.Serve;

namespace Assembly.Tests.Infrastructure.Http;

/// <summary>The check the update helper runs after an install on quit, on the real program's code.</summary>
public sealed class CheckStartTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("assembly-check-start-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public void It_opens_and_migrates_the_database_and_says_yes()
    {
        var database = Path.Combine(_home, "assembly.db");
        Assert.Equal(0, DesktopServe.CheckStart(database));
        using var opened = TestDatabase.Open(database);
    }

    [Fact]
    public void A_database_it_cannot_open_says_no_rather_than_crashing()
    {
        var database = Path.Combine(_home, "assembly.db");
        File.WriteAllText(database, "this is not a database, and it is long enough to have a header that is not SQLite's");
        var stderr = Console.Error;
        Console.SetError(TextWriter.Null);
        try { Assert.NotEqual(0, DesktopServe.CheckStart(database)); }
        finally { Console.SetError(stderr); }
    }
}
