using Assembly.Desktop.Serve;
using Assembly.Remote.Serve;

namespace Assembly.Tests.Infrastructure.Http;

public sealed class NoAgentsFileTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("assembly-no-agents-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public async Task Both_hosts_with_no_agents_file_end_with_an_error_and_open_no_database()
    {
        var desktop = Path.Combine(_home, "desktop");
        var host = Path.Combine(_home, "host");
        Directory.CreateDirectory(desktop);
        var missing = Path.Combine(_home, "none.json");
        var errors = new StringWriter();
        var stderr = Console.Error;
        Console.SetError(errors);
        try
        {
            Assert.Equal(1, await DesktopServe.MainAsync(["--home", desktop, "--token-file", Path.Combine(desktop, "env"), "--agents", missing]));
            Assert.Equal(1, await ServeRun.RunAsync(new(0, host, Path.Combine(host, "env"), missing), null, null, null, default));
        }
        finally
        {
            Console.SetError(stderr);
        }

        Assert.Contains($"There's no agents file at {missing}", errors.ToString());
        Assert.False(File.Exists(Path.Combine(desktop, "assembly.db")));
        Assert.False(File.Exists(Path.Combine(host, "assembly.db")));
    }
}
