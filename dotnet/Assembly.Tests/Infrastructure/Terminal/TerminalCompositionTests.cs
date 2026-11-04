using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Desktop.Serve;
using Assembly.Application.Common.Terminals;
using System.Text;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Terminal;
using Assembly.Remote.Serve;
using Assembly.Tests.Features.Chat.Sessions;
using Assembly.Tests.Infrastructure.Http;

namespace Assembly.Tests.Infrastructure.Terminal;

public sealed class TerminalCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_host_remains_available_with_a_retained_login_shell_failure_and_explicit_shell_still_works(bool remote)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        const string token = "terminal-composition-token";
        var home = Directory.CreateTempSubdirectory("assembly-terminal-composition-").FullName;
        using var stop = new CancellationTokenSource();
        Task<int>? serving = null;
        DesktopBackend? desktop = null;
        Process? child = null, directChild = null;
        IProgramTerminals? programs = null;
        var lookups = 0;
        Task<(string? Path, string? Error)> Unavailable(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref lookups);
            return Task.FromResult<(string?, string?)>((null, AccountLoginShell.Unavailable));
        }
        try
        {
            Uri address;
            if (remote)
            {
                var tokenFile = Path.Combine(home, "token");
                File.WriteAllText(tokenFile, $"ASSEMBLY_TOKEN={token}\n");
                var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
                serving = ServeRun.RunAsync(new(0, home, tokenFile, Path.Combine(home, "agents.json")),
                    new FakeLauncher(), null, (uri, _) => ready.TrySetResult(uri), stop.Token, loginShell: Unavailable, programTerminals: value => programs = value);
                var first = await Task.WhenAny(ready.Task, serving).WaitAsync(TimeSpan.FromSeconds(15));
                if (first == serving) await serving;
                address = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            else
            {
                desktop = await DesktopBackend.StartAsync(home, token, 0, new FakeLauncher(), TextWriter.Null, loginShell: Unavailable, programTerminals: value => programs = value);
                address = desktop.Address;
            }
            Assert.Equal(1, lookups);
            using var main = await ApiTests.OpenOwnerSocketAsync(address, token);
            var identity = ApiTests.Result(await ApiTests.SocketRequestAsync(main, 1, "engine", "machine", "{}"));
            Assert.False(string.IsNullOrWhiteSpace(identity.GetProperty("machine").GetString()));
            using var terminal = await ApiTests.OpenOwnerSocketAsync(address, token, "/socket/terminal");
            var denied = await ApiTests.SocketRequestAsync(terminal, 1, "terminal", "start", JsonSerializer.Serialize(new { cwd = home }));
            Assert.Equal("error", denied.GetProperty("t").GetString());
            Assert.Equal(AccountLoginShell.Unavailable, denied.GetProperty("message").GetString());
            Assert.Empty(ApiTests.Result(await ApiTests.SocketRequestAsync(terminal, 2, "terminal", "list", "{}")).EnumerateArray());
            var made = ApiTests.Result(await ApiTests.SocketRequestAsync(terminal, 3, "terminal", "start", JsonSerializer.Serialize(new { cwd = home, shell = "/bin/sh" })));
            var id = made.GetProperty("id").GetString()!;
            var pid = made.GetProperty("pid").GetInt32();
            child = Process.GetProcessById(pid);
            Assert.False(child.HasExited);
            Assert.Equal(home, made.GetProperty("cwd").GetString());
            ApiTests.Result(await ApiTests.SocketRequestAsync(terminal, 4, "terminal", "close", JsonSerializer.Serialize(new { terminal = id })));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(child.HasExited);
            Assert.Empty(ApiTests.Result(await ApiTests.SocketRequestAsync(terminal, 5, "terminal", "list", "{}")).EnumerateArray());
            var actualPrograms = Assert.IsAssignableFrom<IProgramTerminals>(programs);
            const string owner = "signin:terminal-composition";
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var direct = await actualPrograms.StartProgramAsync(new ProgramStart("/bin/sh",
                ["-c", "stty -echo; printf ready; read line; printf direct-program-ok; exit 7"],
                new Dictionary<string, string?>(), home, owner), bound.Token);
            directChild = Process.GetProcessById(direct.Pid);
            Assert.False(directChild.HasExited);
            var observed = ApiTests.Result(await ApiTests.SocketRequestAsync(terminal, 6, "terminal", "list", JsonSerializer.Serialize(new { owner })));
            var owned = Assert.Single(observed.EnumerateArray());
            Assert.Equal(direct.Id, owned.GetProperty("id").GetString());
            Assert.Equal(direct.Pid, owned.GetProperty("pid").GetInt32());
            var readyOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<byte[]> ReadProgram()
            {
                using var bytes = new MemoryStream();
                await foreach (var piece in actualPrograms.OutputAsync(direct.Id, bound.Token))
                {
                    bytes.Write(piece.Span);
                    if (Encoding.UTF8.GetString(bytes.ToArray()).StartsWith("ready", StringComparison.Ordinal)) readyOutput.TrySetResult();
                }
                return bytes.ToArray();
            }
            var output = ReadProgram();
            await readyOutput.Task.WaitAsync(bound.Token);
            await actualPrograms.WriteAsync(direct.Id, "go\n"u8.ToArray(), bound.Token);
            Assert.Equal("readydirect-program-ok"u8.ToArray(), await output.WaitAsync(bound.Token));
            Assert.Equal(7, await actualPrograms.ExitedAsync(direct.Id, bound.Token));
            await actualPrograms.CloseAsync(direct.Id, bound.Token);
            await directChild.WaitForExitAsync(bound.Token);
            Assert.True(directChild.HasExited);
            Assert.Empty(ApiTests.Result(await ApiTests.SocketRequestAsync(terminal, 7, "terminal", "list", JsonSerializer.Serialize(new { owner }))).EnumerateArray());
            Assert.Equal(1, lookups);
        }
        finally
        {
            // The actual host owns and joins its PTY, including when a preceding assertion fails.
            if (desktop is not null) await desktop.DisposeAsync();
            stop.Cancel();
            if (serving is not null)
                try { await serving.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (OperationCanceledException) { }
            if (child is not null) { Assert.True(child.HasExited); child.Dispose(); }
            if (directChild is not null) { Assert.True(directChild.HasExited); directChild.Dispose(); }
            Directory.Delete(home, true);
        }
    }
}
