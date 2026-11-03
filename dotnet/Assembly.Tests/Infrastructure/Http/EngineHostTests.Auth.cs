using System.Collections.Concurrent;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Database;
using Assembly.Domain.Sessions;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Engine;
using Assembly.Engine.Hosting;
using Assembly.Engine.Contracts;
using Assembly.Engine.Providers;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Http;

public sealed partial class EngineHostTests
{
    [Theory]
    [InlineData("desktop", false)]
    [InlineData("desktop", true)]
    [InlineData("remote", false)]
    [InlineData("remote", true)]
    public async Task Actual_default_auth_composition_only_produces_three_auth_kinds_and_owns_cancel_cleanup(string host, bool privateHome)
    {
        var home = Directory.CreateTempSubdirectory("assembly-auth-host-").FullName;
        // Both actual host compositions use home/sessions for Assembly checkout storage.
        var checkoutRoot = Directory.CreateDirectory(Path.Combine(home, "sessions")).FullName;
        var sentinel = Path.Combine(checkoutRoot, "notes.md");
        await File.WriteAllTextAsync(sentinel, "owner draft");
        var launcher = new FakeLauncher();
        const string rawHome = "/private/HOST_AUTH_HOME_CANARY";
        if (privateHome)
            launcher.Agents = [launcher.Agents[0] with { VendorFamily = "claude", Env = new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = rawHome } }, launcher.Agents[1]];
        var account = AccountContext.Of(launcher.Agents[0], _ => null);
        var target = "claude/" + account;
        var captured = new AuthEnvironmentLauncher(launcher);
        try
        {
            await using var running = await Host.Start(host, home, null, launcher: captured);
            AssertEmptyOrdinaryCensus(running);
            using var socket = await running.Open();
            await AuthCommand(socket, running, target, new CheckProvider());
            var probe = await launcher.NextAsync();
            var initialized = await probe.ReadAsync("initialize");
            await probe.ReplyAsync(initialized, InitializeWithAgentMethod);
            var opening = await probe.OpenAsync("host-auth-probe");
            Assert.Equal(Path.Combine(home, "probe"), opening.GetProperty("params").GetProperty("cwd").GetString());
            Assert.Equal("host-auth-probe", await probe.CloseAsync());
            await WaitProviderAuth(running, target, "unverified");

            await AuthCommand(socket, running, "successful-auth", new StartSignIn("claude", account, "actual", null));
            var loginLink = await launcher.NextAsync();
            await loginLink.ReplyAsync(await loginLink.ReadAsync("initialize"), InitializeWithAgentMethod);
            var login = await loginLink.ReadAsync("auth/login");
            const string originalUrl = "https://example.invalid/%2f?HOST_URL_CANARY=one%2Btwo";
            await loginLink.RequestAsync(991, "elicitation/create", JsonSerializer.Serialize(new
            {
                mode = "url",
                url = originalUrl,
                elicitationId = "host-url",
                requestId = login.GetProperty("id")
            }));
            await running.WaitState("providers", "successful-auth", "waiting_for_person");
            var challenge = await ProviderCall(socket, "challenge", new { operationId = "successful-auth" });
            Assert.Equal(originalUrl, challenge.GetProperty("link").GetString());
            await ProviderCall(socket, "acknowledgeUrl", new
            {
                operationId = "successful-auth",
                expectedGeneration = challenge.GetProperty("generation").GetInt64(),
                originalUrl,
                machine = running.Runtime.Machine
            });
            Assert.Equal("accept", (await loginLink.ResultAsync()).GetProperty("action").GetString());
            await loginLink.ReplyAsync(login, "{}");
            await running.WaitState("providers", "successful-auth", "signed_in");
            await loginLink.Exited.WaitAsync(Timeout);
            Assert.True(loginLink.Disposed);
            await WaitProviderAuth(running, target, "signed_in");
            // The Engine deliberately queues a fresh check after committing positive login facts.
            var refreshed = await launcher.NextAsync();
            await refreshed.ReplyAsync(await refreshed.ReadAsync("initialize"), InitializeWithAgentMethod);
            await refreshed.OpenAsync("post-signin-probe");
            Assert.Equal("post-signin-probe", await refreshed.CloseAsync());

            await AuthCommand(socket, running, "cancelled-auth", new StartSignIn("claude", account, "actual", null));
            var cancelled = await launcher.NextAsync();
            await cancelled.ReplyAsync(await cancelled.ReadAsync("initialize"), InitializeWithAgentMethod);
            await cancelled.ReadAsync("auth/login");
            using var bulk = await running.Open(bulk: true);
            var operation = await Entity(bulk, "sign_in", "cancelled-auth");
            var cancel = new WindowCommand(Guid.NewGuid(), "cancelled-auth", operation.GetProperty("version").GetInt64(), 0, new CancelSignIn(), running.Runtime.Machine);
            var reply = await Call(socket, "command", JsonSerializer.SerializeToElement(cancel, EngineSocketJson.Default.WindowCommand));
            Assert.True(reply.GetProperty("applied").GetBoolean(), reply.ToString());
            await running.WaitState("providers", "cancelled-auth", "cancelled");
            await cancelled.Exited.WaitAsync(Timeout);
            Assert.True(cancelled.Disposed);

            AssertEmptyOrdinaryCensus(running);
            var kinds = ((IEngineRuntime)running.Runtime).Read(scope => scope.Query(
                "SELECT DISTINCT kind FROM engine_outbox ORDER BY kind", row => row.Text(0)), default);
            Assert.Equal(["begin_sign_in", "cancel_sign_in", "probe_provider"], kinds);

            Assert.Equal("owner draft", await File.ReadAllTextAsync(sentinel));
            Assert.Equal(["notes.md"], Directory.EnumerateFileSystemEntries(checkoutRoot).Select(Path.GetFileName));
            Assert.Equal(["claude", "claude", "claude", "claude"], launcher.Launched);
            Assert.False(refreshed.Disposed); // Cancelling the dedicated attempt preserves the unrelated shared adapter.
            Assert.True(probe.Disposed); // Successful sign-in retired the exact idle shared probe instance.
            Assert.Equal(4, captured.Environments.Count);
            if (privateHome)
            {
                Assert.True(AccountContext.IsValid(account));
                Assert.All(captured.Environments, map => Assert.True(map.TryGetValue("CLAUDE_CONFIG_DIR", out var value) && value == rawHome));
                using var read = await running.Open(bulk: true);
                var provider = await Entity(read, "provider", target);
                Assert.Equal(account, provider.GetProperty("accountContext").GetString());
                Assert.False(provider.GetRawText().Contains(rawHome, StringComparison.Ordinal));
                var operationView = await Entity(read, "sign_in", "successful-auth");
                Assert.Equal(account, operationView.GetProperty("accountContext").GetString());
                Assert.False(operationView.GetRawText().Contains(rawHome, StringComparison.Ordinal));
                var leaks = ((IEngineRuntime)running.Runtime).Read(scope => new[]
                {
                    scope.QueryFirst("SELECT COUNT(*) FROM engine_provider WHERE instr(id,$raw)>0 OR instr(account_context,$raw)>0", row => row.Long(0), new SqlArg("$raw", rawHome)),
                    scope.QueryFirst("SELECT COUNT(*) FROM engine_sign_in WHERE instr(account_context,$raw)>0", row => row.Long(0), new SqlArg("$raw", rawHome)),
                    scope.QueryFirst("SELECT COUNT(*) FROM engine_event WHERE instr(entity_id,$raw)>0 OR instr(data_json,$raw)>0", row => row.Long(0), new SqlArg("$raw", rawHome)),
                    scope.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE instr(id,$raw)>0 OR instr(order_key,$raw)>0 OR instr(payload_json,$raw)>0 OR instr(COALESCE(receipt_json,''),$raw)>0", row => row.Long(0), new SqlArg("$raw", rawHome))
                }, default);
                Assert.All(leaks, count => Assert.Equal(0, count));
            }
        }
        finally { Directory.Delete(home, true); }
    }

    private sealed class AuthEnvironmentLauncher(IAgentLauncher inner) : IAgentLauncher
    {
        internal ConcurrentQueue<IReadOnlyDictionary<string, string>> Environments { get; } = new();
        public IReadOnlyList<AgentDescriptor> Agents => inner.Agents;
        public bool CanRunTerminalAuthentication => inner.CanRunTerminalAuthentication;
        public void ValidateFolder(string folder) => inner.ValidateFolder(folder);
        public Task<IAgentLink> LaunchAsync(string agent, StageTimes stages, CancellationToken ct) => inner.LaunchAsync(agent, stages, ct);
        public Task<IAgentLink> LaunchConfiguredAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment, StageTimes stages, CancellationToken ct)
        {
            Environments.Enqueue(environment.ToDictionary(pair => pair.Key, pair => pair.Value));
            return inner.LaunchConfiguredAsync(descriptor, environment, stages, ct);
        }
        public Task<IAgentLink> LaunchSignInAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment,
            IPermissionPolicy policy, StageTimes stages, CancellationToken ct)
        {
            Environments.Enqueue(environment.ToDictionary(pair => pair.Key, pair => pair.Value));
            return inner.LaunchSignInAsync(descriptor, environment, policy, stages, ct);
        }
    }

    private static async Task AuthCommand(System.Net.WebSockets.ClientWebSocket socket, Host running, string id, EngineCommand command)
    {
        var args = new WindowCommand(Guid.NewGuid(), id, 0, 0, command, running.Runtime.Machine);
        var result = await Call(socket, "command", JsonSerializer.SerializeToElement(args, EngineSocketJson.Default.WindowCommand));
        Assert.True(result.GetProperty("applied").GetBoolean(), result.ToString());
    }

    private static async Task<JsonElement> ProviderCall(System.Net.WebSockets.ClientWebSocket socket, string method, object args)
    {
        await Send(socket, new { t = "call", id = 1, feature = "providers", method, args });
        var frame = await Read(socket);
        Assert.Equal("result", frame.GetProperty("t").GetString());
        return frame.GetProperty("result").Clone();
    }

    private const string InitializeWithAgentMethod = """{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[{"methodId":"actual","name":"Actual","type":"agent"}]}""";

    private static void AssertEmptyOrdinaryCensus(Host running)
    {
        var counts = ((IEngineRuntime)running.Runtime).Read(scope => new[]
        {
            scope.QueryFirst("SELECT COUNT(*) FROM engine_session", row => row.Long(0)),
            scope.QueryFirst("SELECT COUNT(*) FROM engine_message", row => row.Long(0)),
            scope.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", row => row.Long(0)),
            scope.QueryFirst("SELECT COUNT(*) FROM engine_run", row => row.Long(0)),
            scope.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE kind NOT IN ('probe_provider','begin_sign_in','cancel_sign_in') AND state NOT IN ('done','failed','dead')", row => row.Long(0))
        }, default);
        Assert.All(counts, count => Assert.Equal(0, count));
    }

    private static async Task WaitProviderAuth(Host running, string id, string auth)
    {
        using var socket = await running.Open();
        await Send(socket, new { t = "sub", id = 7, stream = "engine", topic = "providers", key = id });
        while (true)
        {
            var page = await Read(socket);
            var snapshot = page.GetProperty("t").GetString() == "engine_snapshot";
            foreach (var item in page.GetProperty(snapshot ? "items" : "events").EnumerateArray())
            {
                if (snapshot && item.GetProperty("id").GetString() == id && item.TryGetProperty("authState", out var value) && value.GetString() == auth) return;
                if (!snapshot && item.GetProperty("entityId").GetString() == id && item.GetProperty("data").GetProperty("state").GetString() == auth) return;
            }
        }
    }
}
