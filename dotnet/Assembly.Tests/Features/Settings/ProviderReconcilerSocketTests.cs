using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Desktop.Serve;
using Assembly.Desktop.Settings;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Updates;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Features.Settings;

public sealed class ProviderReconcilerSocketTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private const string Token = "test-reconcile-token";

    private sealed class GatedUpdateSource : IUpdateSource
    {
        private int _updateCalls;
        internal int UpdateCalls => Volatile.Read(ref _updateCalls);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<UpdateWanted> WantedAsync(CancellationToken ct, bool fresh = false) =>
            Task.FromResult(new UpdateWanted(
                App: null,
                Backend: null,
                Adapters: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["claude-agent-acp"] = "2.0.0",
                    ["codex-acp"] = "2.0.0",
                },
                Packaging: "sources",
                Clis: new Dictionary<string, string?>(StringComparer.Ordinal)));

        public bool StartsRemoteUpdates(string machine) => false;

        public Task InstallAsync(string machine, Action<string> progress, CancellationToken ct) => Task.CompletedTask;

        public async Task UpdateAsync(IReadOnlyList<MachineBehind> machines, bool automatic, Action<string> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref _updateCalls);
            progress("Installing adapters…");
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
    }

    [Fact]
    public async Task Two_triggers_at_once_do_not_install_twice()
    {
        var tempRoot = Directory.CreateTempSubdirectory("assembly-reconcile-socket-").FullName;
        var home = Directory.CreateDirectory(Path.Combine(tempRoot, "desktop")).FullName;
        var launcher = new FakeLauncher
        {
            Agents =
            [
                new("claude", "Claude Code", "claude-agent-acp", [], null, VendorFamily: "claude"),
                new("codex", "Codex", "codex-acp", [], null, VendorFamily: "codex"),
            ]
        };
        var source = new GatedUpdateSource();

        try
        {
            var homeLock = HomeLock.Take(home);
            await using var backend = await DesktopBackend.StartAsync(
                homeLock,
                home,
                Token,
                port: 0,
                launcher,
                TextWriter.Null,
                remote: null,
                tunnel: null,
                utilityClis: null,
                savedRemote: null,
                engineEffects: null,
                engineStarted: null,
                agentsFile: null,
                loginShell: _ => Task.FromResult<(string?, string?)>((null, null)),
                programTerminals: null,
                givenInstall: null,
                givenUpdateSource: source);

            using var summarySocket = await OpenSocketAsync(backend.Address);
            using var firstSocket = await OpenSocketAsync(backend.Address);
            using var secondSocket = await OpenSocketAsync(backend.Address);

            await SendAsync(summarySocket, new { t = "sub", id = 1, topic = SettingsStateFeature.SummaryTopic, key = SettingsStateFeature.CurrentKey });
            var initial = await ReadAsync(summarySocket);
            Assert.Equal("state", initial.GetProperty("t").GetString());
            var initialSummary = Assert.Single(initial.GetProperty("items").EnumerateArray()).GetProperty("item");
            Assert.False(initialSummary.GetProperty("working").GetBoolean());

            // Trigger 1 over /socket enters the gated adapter install.
            await SendAsync(firstSocket, new
            {
                t = "call",
                id = 2,
                feature = LocalProviderReconciler.FeatureName,
                method = LocalProviderReconciler.ReconcileMethod,
                args = new { machine = "local" }
            });
            await source.Entered.Task.WaitAsync(Wait);

            // Summary topic pushes working: true while the run is in progress.
            var working = await WaitForSummaryAsync(summarySocket, item => item.GetProperty("working").GetBoolean());
            Assert.True(working.GetProperty("working").GetBoolean());

            // Trigger 2 arrives concurrently while trigger 1 is still installing; also fire app-start trigger.
            var coalesced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.Reconciler!.Coalesced += () => coalesced.TrySetResult();
            backend.BeginUpdateChecks();
            await SendAsync(secondSocket, new
            {
                t = "call",
                id = 3,
                feature = LocalProviderReconciler.FeatureName,
                method = LocalProviderReconciler.ReconcileMethod,
                args = new { machine = "local" }
            });
            await coalesced.Task.WaitAsync(Wait);

            // Release the single in-flight install and await both callers.
            source.Release.TrySetResult();

            var firstReply = await ReadAsync(firstSocket);
            var secondReply = await ReadAsync(secondSocket);
            Assert.Equal("result", firstReply.GetProperty("t").GetString());
            Assert.Equal("result", secondReply.GetProperty("t").GetString());
            Assert.True(firstReply.GetProperty("result").GetProperty("started").GetBoolean());
            Assert.False(secondReply.GetProperty("result").GetProperty("started").GetBoolean());

            // Both concurrent triggers coalesced: the adapter installer ran exactly once.
            Assert.Equal(1, source.UpdateCalls);

            // Summary topic returns to working: false once the run ends.
            var settled = await WaitForSummaryAsync(summarySocket, item => !item.GetProperty("working").GetBoolean());
            Assert.False(settled.GetProperty("working").GetBoolean());

            summarySocket.Abort();
            firstSocket.Abort();
            secondSocket.Abort();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static async Task<JsonElement> WaitForSummaryAsync(ClientWebSocket socket, Func<JsonElement, bool> predicate)
    {
        using var deadline = new CancellationTokenSource(Wait);
        while (true)
        {
            var frame = await ReadAsync(socket, deadline.Token);
            if (frame.GetProperty("t").GetString() != "change")
                continue;
            var item = frame.GetProperty("item");
            if (predicate(item))
                return item.Clone();
        }
    }

    private static async Task<ClientWebSocket> OpenSocketAsync(Uri address)
    {
        var socket = new ClientWebSocket();
        using var stop = new CancellationTokenSource(Wait);
        await socket.ConnectAsync(new Uri($"ws://{address.Authority}/socket"), stop.Token);
        await SendAsync(socket, new { t = "hello", protocol = 1, token = Token });
        Assert.Equal("welcome", (await ReadAsync(socket, stop.Token)).GetProperty("t").GetString());
        return socket;
    }

    private static Task SendAsync(ClientWebSocket socket, object value) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> ReadAsync(ClientWebSocket socket, CancellationToken cancel = default)
    {
        using var stop = cancel.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancel)
            : new CancellationTokenSource(Wait);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token);
            Assert.Equal(WebSocketMessageType.Text, part.MessageType);
            output.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        using var json = JsonDocument.Parse(output.ToArray());
        return json.RootElement.Clone();
    }
}
