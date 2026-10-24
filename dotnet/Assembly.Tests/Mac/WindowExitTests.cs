using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Assembly.Tests.Mac;

/// <summary>
/// A real window process (Photino, on screen for a few seconds) with a tunnel and an adapter running, stopped the ways
/// it can stop. A fake ssh on PATH stands in for the tunnel and <c>/bin/sleep</c> for the adapter; both ignore a closed
/// stdin, so only the window's own cleanup, or the tunnel's wrapper, can end them.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class WindowExitTests : IDisposable
{
    private const int SigKill = 9;
    private const int SigTerm = 15;

    private readonly string _dir = Directory.CreateTempSubdirectory("assembly-window-").FullName;
    private readonly List<int> _seen = [];

    [Fact]
    public async Task Sigterm_stops_the_tunnel_and_the_adapter_with_the_window()
    {
        var window = await StartWindowWithTunnelAndAdapterAsync();

        _ = kill(window.Id, SigTerm);

        await Until(() => window.HasExited, TimeSpan.FromSeconds(30));
        await Until(() => _seen.All(pid => kill(pid, 0) != 0), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_killed_window_still_takes_its_tunnel_with_it()
    {
        var window = await StartWindowWithTunnelAndAdapterAsync();
        var tunnel = Descendants(window.Id).Where(process => process.Command.Contains(" -N ")).Select(process => process.Pid).ToList();

        _ = kill(window.Id, SigKill);

        await Until(() => tunnel.All(pid => kill(pid, 0) != 0), TimeSpan.FromSeconds(5));
    }

    private async Task<Process> StartWindowWithTunnelAndAdapterAsync()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_dir, "bin")).FullName;
        var ssh = Path.Combine(bin, "ssh");
        // The token read answers at once; the forward sleeps (and holds the port, unless a real tunnel already does).
        await File.WriteAllTextAsync(ssh, """
            #!/usr/bin/perl
            use IO::Socket::INET;
            if (!grep { $_ eq "-N" } @ARGV) { print "ASSEMBLY_TOKEN=box\n"; exit 0 }
            my $port = IO::Socket::INET->new(LocalAddr => "127.0.0.1:19797", Listen => 1, ReuseAddr => 1);
            sleep 600;
            """);
        File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var app = Directory.CreateDirectory(Path.Combine(_dir, "app")).FullName;
        await File.WriteAllTextAsync(Path.Combine(app, "index.html"), "<!doctype html><title>exit test</title>");
        var repo = Directory.CreateDirectory(Path.Combine(_dir, "repo")).FullName;
        Run("git", "init", "--quiet", repo);

        var agents = Path.Combine(_dir, "agents.json");
        await File.WriteAllTextAsync(agents, """[{"id":"claude","name":"Claude Code","command":"/bin/sleep","args":["601.5"]}]""");
        var port = FreePort();
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Assembly.Mac")) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var arg in new[]
                 {
                     "window", "--app", app, "--port", $"{port}", "--home", Path.Combine(_dir, "home"), "--token-file", Path.Combine(_dir, "env"),
                     "--remote", "fake-box", "--agents", agents,
                 })
            start.ArgumentList.Add(arg);
        start.Environment["PATH"] = $"{bin}:{Environment.GetEnvironmentVariable("PATH")}";
        var window = Process.Start(start)!;
        _seen.Add(window.Id);

        await Until(() => Listening(port), TimeSpan.FromSeconds(30));
        var token = File.ReadAllLines(Path.Combine(_dir, "env")).Single(line => line.StartsWith("ASSEMBLY_TOKEN=", StringComparison.Ordinal))["ASSEMBLY_TOKEN=".Length..];
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var created = await http.PostAsync("/threads", Json($$"""{"agent":"claude","repoUrl":{{JsonSerializer.Serialize(repo)}}}"""));
        var threadId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("threadId").GetString();
        (await http.PostAsync($"/threads/{threadId}/send", Json("""{"text":"hi"}"""))).EnsureSuccessStatusCode();

        // The adapter never answers initialize, so it stays up; the tunnel's ssh runs under its wrapper.
        await Until(() => Descendants(window.Id) is var all
                          && all.Any(process => process.Command.Contains("sleep 601.5"))
                          && all.Any(process => process.Command.Contains(" -N ")), TimeSpan.FromSeconds(15));
        _seen.AddRange(Descendants(window.Id).Select(process => process.Pid));
        return window;
    }

    private static List<(int Pid, string Command)> Descendants(int root)
    {
        var processes = Run("ps", "-axo", "pid=,ppid=,command=").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries))
            .Select(parts => (Pid: int.Parse(parts[0]), Parent: int.Parse(parts[1]), Command: parts.Length > 2 ? parts[2] : ""))
            .ToList();
        var found = new HashSet<int> { root };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var process in processes)
                grew |= found.Contains(process.Parent) && found.Add(process.Pid);
        }
        return processes.Where(process => process.Pid != root && found.Contains(process.Pid)).Select(process => (process.Pid, process.Command)).ToList();
    }

    private static string Run(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool Listening(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task Until(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(50);
        }
    }

    // Whatever a failed test left: only the processes this test started, by pid.
    public void Dispose()
    {
        foreach (var pid in _seen)
            _ = kill(pid, SigKill);
        Directory.Delete(_dir, recursive: true);
    }

    [DllImport("libc")]
    private static extern int kill(int pid, int signal);
}
