using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Desktop.Serve;
using Assembly.Engine;
using Assembly.Infrastructure.Remote;
using Assembly.Remote.Serve;
using Assembly.Tests.Features.Chat.Sessions;
using Assembly.Tests.Infrastructure.Http;

namespace Assembly.Tests.Infrastructure.Updates;

/// <summary>A real desktop backend and a real remote host: what the check shows comes over the same socket a person's app uses.</summary>
public sealed class UpdateCheckCompositionTests
{
    [Fact]
    public async Task The_check_reads_a_real_remote_and_pushes_what_it_found_on_the_machine()
    {
        const string token = "update-check-token", route = "workbox-test";
        var temporary = Directory.CreateTempSubdirectory("assembly-update-check-").FullName;
        using var stop = new CancellationTokenSource();
        Task<int>? serving = null;
        try
        {
            var remoteHome = Directory.CreateDirectory(Path.Combine(temporary, "remote")).FullName;
            var desktopHome = Directory.CreateDirectory(Path.Combine(temporary, "desktop")).FullName;
            var tokenFile = Path.Combine(remoteHome, "token");
            File.WriteAllText(tokenFile, $"ASSEMBLY_TOKEN={token}\n");
            var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            serving = ServeRun.RunAsync(new(0, remoteHome, tokenFile, Path.Combine(remoteHome, "agents.json")),
                new FakeLauncher(), null, (address, _) => ready.TrySetResult(address), stop.Token);
            if (await Task.WhenAny(ready.Task, serving).WaitAsync(TimeSpan.FromSeconds(15)) == serving) await serving;
            var address = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await using var desktop = await DesktopBackend.StartAsync(desktopHome, token, 0, new FakeLauncher(), TextWriter.Null,
                remote: new RemoteConfig(route, address, token));
            using var socket = await ApiTests.OpenOwnerSocketAsync(desktop.Address, token);
            await socket.SendAsync(Encoding.UTF8.GetBytes("""{"t":"sub","id":1,"topic":"machines"}"""), WebSocketMessageType.Text, true, default);
            await desktop.Connections.ReadyAsync(route);
            desktop.BeginUpdateChecks();

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var buffer = new byte[64 * 1024];
            JsonElement shown = default;
            while (shown.ValueKind != JsonValueKind.Array)
            {
                var received = await socket.ReceiveAsync(buffer, deadline.Token);
                using var frame = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
                if (frame.RootElement.GetProperty("t").GetString() == "change" && frame.RootElement.GetProperty("key").GetString() == route &&
                    frame.RootElement.GetProperty("item").TryGetProperty("updates", out var updates))
                    shown = updates.Clone();
            }
            var items = shown.EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!);
            // A delivered app with no release to compare with never calls the backend behind.
            Assert.Equal("current", items["Backend"].GetProperty("state").GetString());
            // The remote has no adapter bundle. "behind" rather than "unknown" proves its own host answered the read.
            Assert.Equal("behind", items["codex-acp"].GetProperty("state").GetString());
            Assert.Equal("behind", items["claude-agent-acp"].GetProperty("state").GetString());
        }
        finally
        {
            await stop.CancelAsync();
            if (serving is not null) await serving;
            Directory.Delete(temporary, true);
        }
    }
}
