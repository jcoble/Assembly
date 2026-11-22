using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Desktop.Serve;
using Assembly.Desktop.Settings;
using Assembly.Domain.Events;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote.Install;
using Assembly.Infrastructure.Updates;
using Assembly.Tests.Features.Chat.Sessions;
using Assembly.Tests.Infrastructure.Http;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

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

    private sealed class FailingUpdateSource : IUpdateSource
    {
        internal const string Secret = "SECRET_INSTALLER_OUTPUT_CANARY";

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

        public Task UpdateAsync(IReadOnlyList<MachineBehind> machines, bool automatic, Action<string> progress, CancellationToken ct) =>
            throw new CallFailed($"npm ERR! raw installer output {Secret}", "update_failed");
    }

    // A failed local update's raw installer output must never reach pushed state: the note is a fixed sentence.
    [Fact]
    public async Task A_failed_local_update_pushes_a_fixed_sentence_never_the_raw_output()
    {
        var tempRoot = Directory.CreateTempSubdirectory("assembly-reconcile-failure-").FullName;
        var home = Directory.CreateDirectory(Path.Combine(tempRoot, "desktop")).FullName;
        var launcher = new FakeLauncher
        {
            Agents =
            [
                new("claude", "Claude Code", "claude-agent-acp", [], null, VendorFamily: "claude"),
                new("codex", "Codex", "codex-acp", [], null, VendorFamily: "codex"),
            ]
        };

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
                givenUpdateSource: new FailingUpdateSource());

            using var stateSocket = await OpenSocketAsync(backend.Address);
            using var cmdSocket = await OpenSocketAsync(backend.Address);
            await SendAsync(stateSocket, new { t = "sub", id = 1, topic = SettingsStateFeature.Topic });
            await SendAsync(cmdSocket, new
            {
                t = "call",
                id = 2,
                feature = LocalProviderReconciler.FeatureName,
                method = LocalProviderReconciler.ReconcileMethod,
                args = new { machine = "local" }
            });
            Assert.Equal("result", (await ReadAsync(cmdSocket)).GetProperty("t").GetString());

            var failed = await WaitForStateAsync(stateSocket, state => LocalUpdateState(state) == MachineUpdateState.Failed);
            var note = Local(failed).GetProperty("machine").GetProperty("updateNote").GetString();
            Assert.Equal("Update failed: The update didn't finish.", note);
            Assert.DoesNotContain(FailingUpdateSource.Secret, failed.GetRawText(), StringComparison.Ordinal);

            stateSocket.Abort();
            cmdSocket.Abort();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // This machine's own tools wait only for this machine's runs. A run left open on a remote never holds them, and a local one
    // is a wait the user sees (amber, "waiting for running sessions"), not a failure; the update goes on when the run ends.
    private sealed class LocalCliSource : IUpdateSource
    {
        internal Func<RemoteReleaseJobs>? Release { get; set; }

        public Task<UpdateWanted> WantedAsync(CancellationToken ct, bool fresh = false) =>
            Task.FromResult(new UpdateWanted(null, null, new Dictionary<string, string?>(StringComparer.Ordinal), null,
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["claude"] = "2.0.0" }));

        public bool StartsRemoteUpdates(string machine) => false;

        public Task InstallAsync(string machine, Action<string> progress, CancellationToken ct) => Task.CompletedTask;

        public Task UpdateAsync(IReadOnlyList<MachineBehind> machines, bool automatic, Action<string> progress, CancellationToken ct) =>
            Release!().UpdateToolsAsync(null, ToolsBehind.Of(machines.Single().Behind), progress, ct);
    }

    [Fact]
    public async Task A_remote_run_never_holds_this_machines_update_and_a_local_run_makes_it_wait_and_then_it_goes_on_by_itself()
    {
        if (OperatingSystem.IsWindows()) return;
        var tempRoot = Directory.CreateTempSubdirectory("assembly-reconcile-wait-").FullName;
        var home = Directory.CreateDirectory(Path.Combine(tempRoot, "desktop")).FullName;
        var cli = Path.Combine(Directory.CreateDirectory(Path.Combine(tempRoot, "bin")).FullName, "claude");
        var version = cli + ".version";
        await File.WriteAllTextAsync(version, "1.0.0");
        await File.WriteAllTextAsync(cli, "#!/bin/sh\ncase \"$1\" in --version) echo \"$(cat \"$0.version\") (Claude Code)\" ;; update) echo 2.0.0 >\"$0.version\" ;; esac\n");
        File.SetUnixFileMode(cli, (UnixFileMode)0b111_101_101);
        var agentsPath = Path.Combine(home, "agents.json");
        await File.WriteAllTextAsync(agentsPath,
            $$"""[{"id":"claude","name":"Claude Code","command":"claude-agent-acp","args":[],"vendorFamily":"claude","cli":"{{cli}}"}]""");

        // A fresh profile whose only open turn is a session on another machine, which is not answering.
        using (var db = AppDatabase.Open(Path.Combine(home, "assembly.db"), TimeProvider.System, new TopicSet()))
        {
            var rows = RowStore.Open(db);
            var sessions = SessionStore.Open(db, TimeProvider.System, Path.Combine(home, "sessions"), SessionSummaries.Load(rows));
            sessions.Add("remote-session", "claude", "repo", "Workbox");
            rows.Commit("remote-session", new RowConverter().Apply(new TurnStarted(1, "run-1", "still running over there")));
            // Only Claude Code is on, so nothing here depends on which CLIs the computer running the test has.
            ProviderEnablementStore.EnsureSchema(db);
            var enablement = new ProviderEnablementStore(db, TimeProvider.System);
            enablement.SetEnabled("claude", true);
            enablement.SetEnabled("codex", false);
        }

        var launcher = new FakeLauncher
        {
            Agents = [new("claude", "Claude Code", "claude-agent-acp", [], null, Cli: cli, VendorFamily: "claude")]
        };
        var source = new LocalCliSource();

        try
        {
            var homeLock = HomeLock.Take(home);
            await using var backend = await DesktopBackend.StartAsync(
                homeLock, home, Token, port: 0, launcher, TextWriter.Null, remote: null, tunnel: null, utilityClis: null, savedRemote: null,
                engineEffects: null, engineStarted: null, agentsFile: new AgentsFile(agentsPath),
                loginShell: _ => Task.FromResult<(string?, string?)>((null, null)), programTerminals: null, givenInstall: null,
                givenUpdateSource: source);
            source.Release = () => backend.RemoteReleases;

            using var stateSocket = await OpenSocketAsync(backend.Address);
            await SendAsync(stateSocket, new { t = "sub", id = 1, topic = SettingsStateFeature.Topic });

            // 1. Only a remote's run is open: this machine's tools update, and nothing says failed.
            backend.BeginUpdateChecks();
            var updated = await WaitForStateAsync(stateSocket, state => ClaudeCliCurrent(state) || LocalUpdateState(state) == MachineUpdateState.Failed);
            Assert.True(ClaudeCliCurrent(updated), Local(updated).GetRawText());
            Assert.Equal("2.0.0", (await File.ReadAllTextAsync(version)).Trim());

            // The check that followed the update looks at the adapter once.
            var probe = await launcher.NextAsync();
            await probe.ReplyAsync(
                await probe.ReadAsync("initialize"),
                """{"protocolVersion":2,"info":{"name":"claude-agent-acp","version":"0.14.2","title":"Claude Code"},"capabilities":{"session":{}},"authMethods":[]}""");
            await probe.OpenAsync("probe-claude", """[{"configId":"model","name":"Model","category":"model","type":"select","currentValue":"m","options":[{"value":"m","name":"M"}]}]""");
            await probe.CloseAsync();
            await WaitForStateAsync(stateSocket, state => state.GetProperty("providers").EnumerateArray()
                .Single(provider => provider.GetProperty("id").GetString() == "claude").GetProperty("machines").EnumerateArray()
                .Any(machine => machine.GetProperty("machineKey").GetString() == "local" && machine.GetProperty("adapterVersion").GetString() == "0.14.2"));

            // 2. A run on this machine is open: the update waits, as amber, and changes nothing.
            await File.WriteAllTextAsync(version, "1.0.0");
            using var owner = await OpenSocketAsync(backend.Address);
            await SendAsync(owner, new { t = "call", id = 1, feature = "sessions", method = "create", args = new { agent = "claude", clientId = Guid.NewGuid().ToString("N") } });
            var created = await ReadAsync(owner);
            Assert.Equal("result", created.GetProperty("t").GetString());
            var sessionId = created.GetProperty("result").GetProperty("sessionId").GetString()!;
            var clientId = await ApiTests.QueueSocketTurnAsync(owner, sessionId);
            // The adapter the check started is the one this session runs on.
            var agent = probe;
            await agent.OpenAsync();
            var prompt = await agent.ReadAsync("session/prompt");
            await agent.InsertAsync(prompt, "local-run");
            using var live = await ApiTests.OpenRowsAsync(backend.Address, Token, sessionId, after: 0);
            ApiTests.RunOfPrompt(await ApiTests.ReadRowsAsync(live, 2), sessionId, clientId);

            using var reconcileSocket = await OpenSocketAsync(backend.Address);
            await SendAsync(reconcileSocket, new
            {
                t = "call",
                id = 9,
                feature = LocalProviderReconciler.FeatureName,
                method = LocalProviderReconciler.ReconcileMethod,
                args = new { machine = "local" }
            });
            var waiting = await WaitForStateAsync(stateSocket, state =>
                LocalUpdateState(state) is MachineUpdateState.WaitingOnSessions or MachineUpdateState.Failed);
            Assert.True(LocalUpdateState(waiting) == MachineUpdateState.WaitingOnSessions, Local(waiting).GetRawText());
            var summary = waiting.GetProperty("summary");
            Assert.Equal(SettingsSummary.WaitingOnSessions, summary.GetProperty("kind").GetString());
            Assert.True(summary.GetProperty("needsUser").GetBoolean());
            Assert.DoesNotContain("failed", Local(waiting).GetProperty("machine").GetProperty("updateNote").GetString() ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Equal("1.0.0", (await File.ReadAllTextAsync(version)).Trim());

            // 3. The run ends: the same reconcile goes on by itself, with no further trigger.
            await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""", prompt.GetProperty("params").GetProperty("sessionId").GetString()!);
            var finished = await WaitForStateAsync(stateSocket, state => ClaudeCliCurrent(state) || LocalUpdateState(state) == MachineUpdateState.Failed);
            Assert.True(ClaudeCliCurrent(finished), Local(finished).GetRawText());
            Assert.Equal("2.0.0", (await File.ReadAllTextAsync(version)).Trim());
            Assert.Equal("result", (await ReadAsync(reconcileSocket)).GetProperty("t").GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static JsonElement Local(JsonElement state) =>
        state.GetProperty("machines").EnumerateArray().Single(machine => machine.GetProperty("key").GetString() == "local");

    private static string? LocalUpdateState(JsonElement state) => Local(state).GetProperty("updateState").GetString();

    private static bool ClaudeCliCurrent(JsonElement state) =>
        Local(state).GetProperty("machine").TryGetProperty("updates", out var updates)
        && updates.EnumerateArray().Any(update => update.GetProperty("kind").GetString() == "cli"
            && update.GetProperty("name").GetString() == "claude" && update.GetProperty("state").GetString() == Assembly.Application.Common.Machines.UpdateItem.UpToDate);

    private static async Task<JsonElement> WaitForStateAsync(ClientWebSocket socket, Func<JsonElement, bool> predicate)
    {
        using var deadline = new CancellationTokenSource(Wait);
        while (true)
        {
            var frame = await ReadAsync(socket, deadline.Token);
            var item = frame.GetProperty("t").GetString() switch
            {
                "state" => Assert.Single(frame.GetProperty("items").EnumerateArray()).GetProperty("item"),
                "change" => frame.GetProperty("item"),
                _ => default
            };
            if (item.ValueKind == JsonValueKind.Object && predicate(item))
                return item.Clone();
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
