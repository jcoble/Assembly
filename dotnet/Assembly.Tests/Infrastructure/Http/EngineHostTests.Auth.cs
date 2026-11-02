using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Engine;
using Assembly.Engine.Hosting;
using Assembly.Engine.Providers;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Http;

public sealed partial class EngineHostTests
{
    [Theory]
    [InlineData("desktop")]
    [InlineData("remote")]
    public async Task Actual_default_auth_composition_only_produces_three_auth_kinds_and_owns_cancel_cleanup(string host)
    {
        var home = Directory.CreateTempSubdirectory("assembly-auth-host-").FullName;
        var launcher = new FakeLauncher();
        try
        {
            await using var running = await Host.Start(host, home, null, launcher: launcher);
            AssertEmptyOrdinaryCensus(running);
            using var socket = await running.Open();
            await Command(socket, "claude/", new CheckProvider());
            var probe = await launcher.NextAsync();
            var initialized = await probe.ReadAsync("initialize");
            await probe.ReplyAsync(initialized, InitializeWithAgentMethod);
            var opening = await probe.OpenAsync("host-auth-probe");
            Assert.Equal(Path.Combine(home, "probe"), opening.GetProperty("params").GetProperty("cwd").GetString());
            Assert.Equal("host-auth-probe", await probe.CloseAsync());
            await WaitProviderAuth(running, "claude/", "unverified");

            await Command(socket, "successful-auth", new StartSignIn("claude", "", "actual", null));
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
            await WaitProviderAuth(running, "claude/", "signed_in");
            // The Engine deliberately queues a fresh check after committing positive login facts.
            var refreshed = await launcher.NextAsync();
            await refreshed.ReplyAsync(await refreshed.ReadAsync("initialize"), InitializeWithAgentMethod);
            await refreshed.OpenAsync("post-signin-probe");
            Assert.Equal("post-signin-probe", await refreshed.CloseAsync());

            await Command(socket, "cancelled-auth", new StartSignIn("claude", "", "actual", null));
            var cancelled = await launcher.NextAsync();
            await cancelled.ReplyAsync(await cancelled.ReadAsync("initialize"), InitializeWithAgentMethod);
            await cancelled.ReadAsync("auth/login");
            using var bulk = await running.Open(bulk: true);
            var operation = await Entity(bulk, "sign_in", "cancelled-auth");
            var cancel = new WindowCommand(Guid.NewGuid(), "cancelled-auth", operation.GetProperty("version").GetInt64(), 0, new CancelSignIn());
            var reply = await Call(socket, "command", JsonSerializer.SerializeToElement(cancel, EngineSocketJson.Default.WindowCommand));
            Assert.True(reply.GetProperty("applied").GetBoolean(), reply.ToString());
            await running.WaitState("providers", "cancelled-auth", "cancelled");
            await cancelled.Exited.WaitAsync(Timeout);
            Assert.True(cancelled.Disposed);

            AssertEmptyOrdinaryCensus(running);
            var kinds = ((IEngineRuntime)running.Runtime).Read(scope => scope.Query(
                "SELECT DISTINCT kind FROM engine_outbox ORDER BY kind", row => row.Text(0)), default);
            Assert.Equal(["begin_sign_in", "cancel_sign_in", "probe_provider"], kinds);
            Assert.Empty(launcher.CheckedOut);
            Assert.Equal(["claude", "claude", "claude", "claude"], launcher.Launched);
            Assert.False(refreshed.Disposed); // Cancelling the dedicated attempt preserves the unrelated shared adapter.
            Assert.True(probe.Disposed); // Successful sign-in retired the exact idle shared probe instance.
        }
        finally { Directory.Delete(home, true); }
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
