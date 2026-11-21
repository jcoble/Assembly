using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Desktop.Serve;
using Assembly.Desktop.Settings;
using Assembly.Engine.Persistence;
using Assembly.Engine.Providers;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Remote.Install;
using Assembly.Infrastructure.Updates;
using Assembly.Remote.Serve;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Features.Chat.Sessions;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Features.Settings;

public sealed class SettingsStateSocketTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private const string LocalToken = "SECRET_LOCAL_TOKEN_CANARY";
    private const string RemoteToken = "SECRET_REMOTE_TOKEN_CANARY";
    private const string SignInFileSecret = "SECRET_SIGNIN_FILE_CANARY";
    private const string EnvApiKeySecret = "SECRET_ENV_API_KEY_CANARY";
    private const string EnvOpenAiSecret = "SECRET_OPENAI_KEY_CANARY";
    private const string EnvPrivateSecret = "SECRET_ENV_VALUE_CANARY";
    private const string AdapterArgSecret = "SECRET_ADAPTER_ARG_CANARY";
    private const string ErrorDetailSecret = "SECRET_ERROR_DETAIL_CANARY";
    private const string InstallOutputSecret = "SECRET_INSTALL_STDERR_CANARY";

    [Fact]
    public async Task Subscribing_returns_composed_settings_state_and_never_leaks_secrets()
    {
        var tempRoot = Directory.CreateTempSubdirectory("assembly-settings-state-").FullName;
        var home = Directory.CreateDirectory(Path.Combine(tempRoot, "desktop")).FullName;
        var remoteHome = Directory.CreateDirectory(Path.Combine(tempRoot, "remote")).FullName;
        var claudeHome = Directory.CreateDirectory(Path.Combine(home, "private-claude-home")).FullName;
        var codexHome = Directory.CreateDirectory(Path.Combine(home, "private-codex-home")).FullName;
        var remoteClaudeHome = Directory.CreateDirectory(Path.Combine(remoteHome, "private-remote-claude-home")).FullName;
        var remoteCodexHome = Directory.CreateDirectory(Path.Combine(remoteHome, "private-remote-codex-home")).FullName;
        var tokenFile = Path.Combine(remoteHome, "remote-env");
        await File.WriteAllTextAsync(Path.Combine(claudeHome, "credentials.json"), $"{{\"accessToken\":\"{SignInFileSecret}\"}}", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(codexHome, "auth.json"), $"{{\"token\":\"{SignInFileSecret}\"}}", CancellationToken.None);
        await File.WriteAllTextAsync(tokenFile, $"ASSEMBLY_TOKEN={RemoteToken}\n", CancellationToken.None);

        string[] secrets =
        [
            LocalToken,
            RemoteToken,
            SignInFileSecret,
            EnvApiKeySecret,
            EnvOpenAiSecret,
            EnvPrivateSecret,
            AdapterArgSecret,
            ErrorDetailSecret,
            InstallOutputSecret,
            claudeHome,
            codexHome,
            remoteClaudeHome,
            remoteCodexHome
        ];

        var launcher = new FakeLauncher
        {
            Agents =
            [
                new("claude", "Claude Code", "claude-agent-acp", [AdapterArgSecret],
                    new Dictionary<string, string>
                    {
                        ["CLAUDE_CONFIG_DIR"] = claudeHome,
                        ["ANTHROPIC_API_KEY"] = EnvApiKeySecret,
                        ["PRIVATE_SECRET"] = EnvPrivateSecret
                    },
                    VendorFamily: "claude"),
                new("codex", "Codex", "codex-acp", [AdapterArgSecret],
                    new Dictionary<string, string>
                    {
                        ["CODEX_HOME"] = codexHome,
                        ["OPENAI_API_KEY"] = EnvOpenAiSecret
                    },
                    VendorFamily: "codex")
            ]
        };

        var remoteLauncher = new FakeLauncher
        {
            Agents =
            [
                new("claude", "Claude Code", "claude-agent-acp", [AdapterArgSecret],
                    new Dictionary<string, string>
                    {
                        ["CLAUDE_CONFIG_DIR"] = remoteClaudeHome,
                        ["ANTHROPIC_API_KEY"] = EnvApiKeySecret
                    },
                    VendorFamily: "claude"),
                new("codex", "Codex", "codex-acp", [AdapterArgSecret],
                    new Dictionary<string, string>
                    {
                        ["CODEX_HOME"] = remoteCodexHome,
                        ["OPENAI_API_KEY"] = EnvOpenAiSecret
                    },
                    VendorFamily: "codex")
            ]
        };

        using var remoteStop = new CancellationTokenSource();
        Task<int>? serving = null;
        EngineRuntime? engine = null;
        try
        {
            var remoteReady = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            serving = ServeRun.RunAsync(
                new(0, remoteHome, tokenFile, Path.Combine(remoteHome, "agents.json")),
                remoteLauncher,
                null,
                (address, _) => remoteReady.TrySetResult(address),
                remoteStop.Token);
            if (await Task.WhenAny(remoteReady.Task, serving).WaitAsync(Wait) == serving)
                await serving;
            var remoteAddress = await remoteReady.Task.WaitAsync(Wait);

            var homeLock = HomeLock.Take(home);
            DesktopBackend? backend = null;
            try
            {
                backend = await DesktopBackend.StartAsync(
                    homeLock,
                    home,
                    LocalToken,
                    port: 0,
                    launcher,
                    TextWriter.Null,
                    remote: new RemoteConfig("Workbox", remoteAddress, RemoteToken),
                    tunnel: null,
                    utilityClis: null,
                    savedRemote: new SavedMachine("Workbox", "workbox-ssh", 9797, SavedMachine.InstalledTokenFile),
                    engineEffects: null,
                    engineStarted: started => engine = started,
                    agentsFile: null,
                    loginShell: _ => Task.FromResult<(string?, string?)>((null, null)),
                    programTerminals: null,
                    givenRemoteShell: new FailingInstallShell(tempRoot, InstallOutputSecret),
                    givenRemotePackages: new StubPackages());
            }
            catch
            {
                homeLock.Dispose();
                throw;
            }
            await using var ownedBackend = backend;

            using var subSocket = await OpenSocketAsync(backend.Address);
            using var machinesSocket = await OpenSocketAsync(backend.Address);
            using var cmdSocket = await OpenSocketAsync(backend.Address);

            await SendAsync(subSocket, new { t = "sub", id = 1, topic = SettingsStateFeature.Topic });
            var initial = await ReadAsync(subSocket);
            Assert.Equal("state", initial.GetProperty("t").GetString());
            Assert.Equal(1, initial.GetProperty("sub").GetInt64());
            AssertNoSecrets(initial.GetRawText(), secrets);

            await SendAsync(machinesSocket, new { t = "sub", id = 10, topic = MachinesFeature.Topic });
            var initialMachines = await ReadAsync(machinesSocket);
            Assert.Equal("state", initialMachines.GetProperty("t").GetString());
            AssertNoSecrets(initialMachines.GetRawText(), secrets);

            var entry = Assert.Single(initial.GetProperty("items").EnumerateArray());
            Assert.Equal(SettingsStateFeature.CurrentKey, entry.GetProperty("key").GetString());
            var item = entry.GetProperty("item");
            var initialSummary = item.GetProperty("summary");
            Assert.Equal(SettingsSummary.Nothing, initialSummary.GetProperty("kind").GetString());
            Assert.Equal("Everything is ready.", initialSummary.GetProperty("message").GetString());

            var machines = item.GetProperty("machines").EnumerateArray().ToArray();
            var localMachine = Assert.Single(machines, m => m.GetProperty("key").GetString() == "local");
            Assert.Equal(MachineConnectionState.Connected, localMachine.GetProperty("connectionState").GetString());
            Assert.Equal("local", localMachine.GetProperty("machine").GetProperty("kind").GetString());
            var remoteMachine = Assert.Single(machines, m => m.GetProperty("key").GetString() == "Workbox");
            Assert.Equal(MachineConnectionState.Connected, remoteMachine.GetProperty("connectionState").GetString());
            Assert.Equal("remote", remoteMachine.GetProperty("machine").GetProperty("kind").GetString());
            Assert.Equal(SavedMachine.InstalledTokenFile, remoteMachine.GetProperty("machine").GetProperty("tokenFile").GetString());
            var remoteEngineMachine = remoteMachine.GetProperty("engineMachine").GetString();
            Assert.False(string.IsNullOrWhiteSpace(remoteEngineMachine));

            var providers = item.GetProperty("providers").EnumerateArray().ToArray();
            var claudeInit = Assert.Single(providers, p => p.GetProperty("id").GetString() == "claude");
            Assert.Equal("Claude Code", claudeInit.GetProperty("name").GetString());
            Assert.Equal(ProviderGlyph.Of("claude"), claudeInit.GetProperty("glyph").GetString());
            Assert.True(claudeInit.GetProperty("enabled").GetBoolean());
            Assert.Equal(JsonValueKind.Null, claudeInit.GetProperty("models").ValueKind);

            var codexInit = Assert.Single(providers, p => p.GetProperty("id").GetString() == "codex");
            Assert.Equal("Codex", codexInit.GetProperty("name").GetString());
            Assert.Equal(ProviderGlyph.Of("codex"), codexInit.GetProperty("glyph").GetString());
            Assert.True(codexInit.GetProperty("enabled").GetBoolean());
            Assert.Equal(JsonValueKind.Null, codexInit.GetProperty("models").ValueKind);

            // Run real provider checks so composed adapter version, shared/per-machine models, sign-in state and summary push live.
            var claudeAccount = AccountContext.Of(launcher.Agents[0], _ => null);
            var codexAccount = AccountContext.Of(launcher.Agents[1], _ => null);
            await SendCheckAsync(cmdSocket, engine!.Machine, $"claude/{claudeAccount}", callId: 2);
            var claudeLink = await launcher.NextAsync();
            await claudeLink.ReplyAsync(
                await claudeLink.ReadAsync("initialize"),
                """{"protocolVersion":2,"info":{"name":"claude-agent-acp","version":"0.14.2","title":"Claude Code"},"capabilities":{"session":{}},"authMethods":[]}""");
            await claudeLink.OpenAsync(
                "probe-claude",
                """[{"configId":"model","name":"Model","category":"model","type":"select","currentValue":"claude-opus-4-6","options":[{"value":"claude-opus-4-6","name":"Claude Opus 4.6"}]}]""");
            await claudeLink.CloseAsync();

            await SendCheckAsync(cmdSocket, engine.Machine, $"codex/{codexAccount}", callId: 3);
            var codexLink = await launcher.NextAsync();
            await codexLink.ReplyAsync(
                await codexLink.ReadAsync("initialize"),
                """{"protocolVersion":2,"info":{"name":"codex-acp","version":"0.9.1","title":"Codex"},"capabilities":{"session":{}},"authMethods":[]}""");
            var codexOpen = await codexLink.ReadAsync("session/new");
            await codexLink.ReplyErrorAsync(codexOpen, -32000, $"Authentication required ({ErrorDetailSecret})");

            var settled = await WaitForStateAsync(subSocket, secrets, candidate =>
            {
                var claudeLocal = ProviderMachine(candidate, "claude", "local");
                var codexLocal = ProviderMachine(candidate, "codex", "local");
                return claudeLocal.GetProperty("adapterVersion").GetString() == "0.14.2"
                    && codexLocal.GetProperty("state").GetString() == ProviderRowState.NeedsSignIn;
            });

            Assert.Equal(SettingsSummary.NeedsSignIn, settled.GetProperty("summary").GetProperty("kind").GetString());
            Assert.False(string.IsNullOrWhiteSpace(settled.GetProperty("summary").GetProperty("message").GetString()));

            var claudeProvider = Provider(settled, "claude");
            var sharedModel = Assert.Single(claudeProvider.GetProperty("models").EnumerateArray());
            Assert.Equal("claude-opus-4-6", sharedModel.GetProperty("id").GetString());

            var claudeFinal = ProviderMachine(settled, "claude", "local");
            Assert.Equal(ProviderRowState.Ready, claudeFinal.GetProperty("state").GetString());
            Assert.Equal("0.14.2", claudeFinal.GetProperty("adapterVersion").GetString());
            var model = Assert.Single(claudeFinal.GetProperty("models").EnumerateArray());
            Assert.Equal("claude-opus-4-6", model.GetProperty("id").GetString());

            var codexProvider = Provider(settled, "codex");
            Assert.Equal(JsonValueKind.Null, codexProvider.GetProperty("models").ValueKind);
            var codexFinal = ProviderMachine(settled, "codex", "local");
            Assert.Equal(ProviderRowState.NeedsSignIn, codexFinal.GetProperty("state").GetString());
            Assert.Equal(ProviderSignInState.NeedsSignIn, codexFinal.GetProperty("signInState").GetString());
            Assert.Equal("0.9.1", codexFinal.GetProperty("adapterVersion").GetString());
            Assert.Equal(JsonValueKind.Null, codexFinal.GetProperty("lastFailure").ValueKind);

            // Check remote Claude with a different model list: provider-level models becomes null while per-machine lists remain.
            var remoteClaudeAccount = AccountContext.Of(remoteLauncher.Agents[0], _ => null);
            await SendCheckAsync(cmdSocket, remoteEngineMachine!, $"claude/{remoteClaudeAccount}", callId: 4);
            var remoteClaudeLink = await remoteLauncher.NextAsync();
            await remoteClaudeLink.ReplyAsync(
                await remoteClaudeLink.ReadAsync("initialize"),
                """{"protocolVersion":2,"info":{"name":"claude-agent-acp","version":"0.14.2","title":"Claude Code"},"capabilities":{"session":{}},"authMethods":[]}""");
            await remoteClaudeLink.OpenAsync(
                "probe-remote-claude",
                """[{"configId":"model","name":"Model","category":"model","type":"select","currentValue":"claude-sonnet-4-6","options":[{"value":"claude-sonnet-4-6","name":"Claude Sonnet 4.6"}]}]""");
            await remoteClaudeLink.CloseAsync();

            var diverged = await WaitForStateAsync(subSocket, secrets, candidate =>
                ProviderMachine(candidate, "claude", "Workbox").GetProperty("models").ValueKind == JsonValueKind.Array);

            Assert.Equal(JsonValueKind.Null, Provider(diverged, "claude").GetProperty("models").ValueKind);
            Assert.Equal("claude-sonnet-4-6", Assert.Single(ProviderMachine(diverged, "claude", "Workbox").GetProperty("models").EnumerateArray()).GetProperty("id").GetString());

            // Run a failing check whose raw error carries a secret canary: lastFailure must be populated and scrubbed.
            var remoteCodexAccount = AccountContext.Of(remoteLauncher.Agents[1], _ => null);
            await SendCheckAsync(cmdSocket, remoteEngineMachine!, $"codex/{remoteCodexAccount}", callId: 5);
            var failingCodexLink = await remoteLauncher.NextAsync();
            var failingInit = await failingCodexLink.ReadAsync("initialize");
            await failingCodexLink.ReplyErrorAsync(failingInit, -32603, $"Adapter exploded with secret {ErrorDetailSecret}");

            var failed = await WaitForStateAsync(subSocket, secrets, candidate =>
                ProviderMachine(candidate, "codex", "Workbox").GetProperty("lastFailure").ValueKind == JsonValueKind.Object);

            Assert.Equal(SettingsSummary.Problem, failed.GetProperty("summary").GetProperty("kind").GetString());
            var lastFailure = ProviderMachine(failed, "codex", "Workbox").GetProperty("lastFailure");
            Assert.False(string.IsNullOrWhiteSpace(lastFailure.GetProperty("message").GetString()));
            // A remote's raw output never leaves its machine: its detail stays the safe code.
            Assert.Equal("adapter_crashed", lastFailure.GetProperty("detail").GetString());

            // A failing check on this machine keeps its raw output as the detail, with the secret scrubbed out.
            var claudeVersion = ProviderMachine(failed, "claude", "local").GetProperty("version").GetInt64();
            await SendCheckAsync(cmdSocket, engine!.Machine, $"claude/{claudeAccount}", callId: 6, claudeVersion);
            // The warm adapter is reused, so the failing answer goes on the first check's link.
            var localFailingOpen = await claudeLink.ReadAsync("session/new");
            await claudeLink.ReplyErrorAsync(localFailingOpen, -32603, $"Adapter exploded with secret {ErrorDetailSecret}");

            var localFailed = await WaitForStateAsync(subSocket, secrets, candidate =>
            {
                var failure = ProviderMachine(candidate, "claude", "local").GetProperty("lastFailure");
                return failure.ValueKind == JsonValueKind.Object
                    && (failure.GetProperty("detail").GetString() ?? "").Contains("Adapter exploded");
            });
            var localDetail = ProviderMachine(localFailed, "claude", "local").GetProperty("lastFailure").GetProperty("detail").GetString();
            Assert.DoesNotContain(ErrorDetailSecret, localDetail);
            // Run a failing remote install whose stderr prints a secret canary: neither settings_state nor machines may push it.
            await SendAsync(cmdSocket, new { t = "call", id = 7, feature = "updates", method = "install", args = new { machine = "Workbox" } });
            var installReply = await ReadAsync(cmdSocket);
            Assert.Equal("result", installReply.GetProperty("t").GetString());
            Assert.True(installReply.GetProperty("result").GetProperty("started").GetBoolean(), installReply.GetRawText());

            var failedInstall = await WaitForStateAsync(subSocket, secrets, candidate =>
                Machine(candidate, "Workbox").GetProperty("updateState").GetString() == MachineUpdateState.Failed);
            var failedRemoteNote = Machine(failedInstall, "Workbox").GetProperty("machine").GetProperty("updateNote").GetString();
            Assert.False(string.IsNullOrWhiteSpace(failedRemoteNote));

            var failedMachinesFrame = await WaitForMachineChangeAsync(machinesSocket, secrets, "Workbox", candidate =>
                candidate.TryGetProperty("updateNote", out var note)
                && note.ValueKind == JsonValueKind.String
                && (!candidate.TryGetProperty("updating", out var updating) || !updating.GetBoolean()));
            Assert.Equal(failedRemoteNote, failedMachinesFrame.GetProperty("updateNote").GetString());
            subSocket.Abort();
            machinesSocket.Abort();
            cmdSocket.Abort();
        }
        finally
        {
            await remoteStop.CancelAsync();
            if (serving is not null)
                await serving;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Concurrent_subscribe_unsubscribe_resubscribe_never_deadlocks_and_still_delivers()
    {
        var tempRoot = Directory.CreateTempSubdirectory("assembly-settings-lock-").FullName;
        var dbPath = Path.Combine(tempRoot, "assembly.db");
        var topics = new TopicSet();
        var db = AppDatabase.Open(dbPath, TimeProvider.System, topics);
        EngineSchema.Open(db, TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds());
        var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var status = new MachineStatus(topics, []);
        var sessions = SessionStore.Open(db, TimeProvider.System, Path.Combine(tempRoot, "sessions"));
        var machinesFeature = new MachinesFeature(new MachineSet(new FakeMachine(null, sessions)), status, () => { }, TimeProvider.System);
        var launcher = new FakeLauncher();
        var feature = new SettingsStateFeature(db, topics, work, launcher, machinesFeature, "local-engine", TimeProvider.System);
        var registry = new TopicCaptureRegistry();
        feature.Register(registry);
        var ownerCall = new CallContext(CancellationToken.None, "owner", Peer: new OwnerPeer());
        var stormDone = false;
        try
        {
            var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 300; i++)
                {
                    var sub = topics.Subscribe(
                        SettingsStateFeature.Topic,
                        null,
                        _ => registry.ReadState!(null, ownerCall),
                        (_, _) => { });
                    topics.Unsubscribe(sub);
                }
            })).ToArray();

            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5));
            stormDone = true;

            var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var liveSub = topics.Subscribe(
                SettingsStateFeature.Topic,
                null,
                _ => registry.ReadState!(null, ownerCall),
                (_, json) => delivered.TrySetResult(json));

            var unwatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void WatchMachinesUnsub(string topic, string? _)
            {
                if (topic == MachinesFeature.Topic && !topics.HasSubscribers(MachinesFeature.Topic))
                    unwatched.TrySetResult();
            }
            topics.Unsubscribed += WatchMachinesUnsub;
            try
            {
                status.Updates(null, [new UpdateItem("desktop", "Desktop", "0.1.0", "0.2.0", UpdateItem.Behind)]);
                var pushed = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Contains("\"update_available\"", pushed, StringComparison.Ordinal);

                topics.Unsubscribe(liveSub);
                await unwatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(topics.HasSubscribers(MachinesFeature.Topic));
            }
            finally
            {
                topics.Unsubscribed -= WatchMachinesUnsub;
            }
        }
        finally
        {
            if (stormDone)
            {
                await feature.DisposeAsync();
                await machinesFeature.DisposeAsync();
                await work.DisposeAsync();
                db.Dispose();
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class FailingInstallShell(string workingDirectory, string secret) : RemoteReleaseShell
    {
        internal override Task<string> RunAsync(string target, string command, Action<string>? line, CancellationToken ct) =>
            RunProcessAsync(
                new AgentCommand("sh", ["-c", $"printf '%s\\n' \"ANTHROPIC_API_KEY={secret}\" >&2; exit 1"], workingDirectory),
                null,
                line,
                ct);
    }

    private sealed class StubPackages : IRemotePackages, IRemotePackage
    {
        public Task<IRemotePackage> PrepareAsync(CancellationToken ct) => Task.FromResult<IRemotePackage>(this);
        public Task<ReceivedPackage> ReceiveAsync(string target, string stage, Action<string> phase, CancellationToken ct) => throw new NotSupportedException();
        public Task<AdapterArguments> InstallAdaptersAsync(string target, string quotedStage, bool keep, Action<string> phase, CancellationToken ct) => throw new NotSupportedException();
        public Task<AdapterArguments?> UpdateToolsAsync(string target, string quotedStage, ToolsBehind tools, Action<string> phase, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class TopicCaptureRegistry : ICallRegistry
    {
        internal Func<string?, CallContext, object>? ReadState { get; private set; }
        public void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result, Func<TArgs, CallContext, Task<TResult>> handler, bool bulk = false) { }
        public void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result, Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler, Func<TArgs, string?>? order = null, Action<TArgs, CallContext>? failed = null, bool bulk = false) { }
        public void Save<TValue>(string feature, JsonTypeInfo<TValue> value, Action<string, TValue, IWriteScope> save) { }
        public void Topic<TItem>(string topic, JsonTypeInfo<TItem> item, Func<TItem, string> key, Func<string?, CallContext, IReadOnlyList<TItem>> state, bool terminal = false) =>
            ReadState = (k, c) => state(k, c);
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state, Action<string, CallContext>? opened = null) { }
        public void Closed(Action<string> connection) { }
    }

    private static async Task<JsonElement> WaitForStateAsync(
        ClientWebSocket socket,
        string[] secrets,
        Func<JsonElement, bool> predicate)
    {
        using var deadline = new CancellationTokenSource(Wait);
        while (true)
        {
            var frame = await ReadAsync(socket, deadline.Token);
            AssertNoSecrets(frame.GetRawText(), secrets);
            Assert.Equal("change", frame.GetProperty("t").GetString());
            var candidate = frame.GetProperty("item");
            if (predicate(candidate))
                return candidate.Clone();
        }
    }

    private static async Task<JsonElement> WaitForMachineChangeAsync(
        ClientWebSocket socket,
        string[] secrets,
        string key,
        Func<JsonElement, bool> predicate)
    {
        using var deadline = new CancellationTokenSource(Wait);
        while (true)
        {
            var frame = await ReadAsync(socket, deadline.Token);
            AssertNoSecrets(frame.GetRawText(), secrets);
            if (frame.GetProperty("t").GetString() != "change" || frame.GetProperty("key").GetString() != key)
                continue;
            var candidate = frame.GetProperty("item");
            if (predicate(candidate))
                return candidate.Clone();
        }
    }

    private static JsonElement Machine(JsonElement state, string machineKey) =>
        state.GetProperty("machines").EnumerateArray()
            .Single(m => m.GetProperty("key").GetString() == machineKey);

    private static JsonElement Provider(JsonElement state, string providerId) =>
        state.GetProperty("providers").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == providerId);

    private static JsonElement ProviderMachine(JsonElement state, string providerId, string machineKey) =>
        Provider(state, providerId)
            .GetProperty("machines").EnumerateArray()
            .Single(m => m.GetProperty("machineKey").GetString() == machineKey);

    private static void AssertNoSecrets(string rawFrame, string[] secrets)
    {
        foreach (var secret in secrets)
            Assert.DoesNotContain(secret, rawFrame, StringComparison.Ordinal);
    }

    private static async Task SendCheckAsync(ClientWebSocket socket, string engineMachine, string entityId, long callId, long expectedVersion = 0)
    {
        var command = new WindowCommand(Guid.NewGuid(), entityId, expectedVersion, 0, new CheckProvider(), engineMachine);
        await SendAsync(socket, new
        {
            t = "call",
            id = callId,
            feature = "engine",
            method = "command",
            args = JsonSerializer.SerializeToElement(command, EngineSocketJson.Default.WindowCommand)
        });
        var reply = await ReadAsync(socket);
        Assert.Equal("result", reply.GetProperty("t").GetString());
        Assert.True(reply.GetProperty("result").GetProperty("applied").GetBoolean(), reply.GetRawText());
    }

    private static async Task<ClientWebSocket> OpenSocketAsync(Uri address)
    {
        var socket = new ClientWebSocket();
        using var stop = new CancellationTokenSource(Wait);
        await socket.ConnectAsync(new Uri($"ws://{address.Authority}/socket"), stop.Token);
        await SendAsync(socket, new { t = "hello", protocol = 1, token = LocalToken });
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
