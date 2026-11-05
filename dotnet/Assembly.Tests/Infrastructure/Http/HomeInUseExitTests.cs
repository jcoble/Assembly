using Assembly.Desktop.Serve;
using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests.Infrastructure.Http;

/// <summary>A window reopened while the update helper holds the home folder: its backend stops cleanly, with its own exit code.</summary>
public sealed class HomeInUseExitTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("assembly-home-in-use-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public async Task A_refused_home_exits_with_its_own_code_and_one_line_and_opens_no_database()
    {
        File.WriteAllText(Path.Combine(_home, "agents.json"), "[]");
        var errors = new StringWriter();
        var stderr = Console.Error;
        Console.SetError(errors);
        try
        {
            using var held = HomeLock.Take(_home);
            Assert.Equal(DesktopServe.HomeInUseExit, await DesktopServe.MainAsync(["--home", _home, "--token-file", Path.Combine(_home, "env"), "--port", "0"]));
        }
        finally
        {
            Console.SetError(stderr);
        }

        var line = Assert.Single(errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains($"home in use by pid {Environment.ProcessId}", line, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_home, "assembly.db")));
    }
}
