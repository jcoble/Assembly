using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Workspaces;
using Assembly.Desktop.Serve;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Features.Chat.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Features.Workspaces;

public sealed class WorkspacesSocketTests : IAsyncLifetime
{
    private readonly TempDatabase file = new();
    private AppDatabase db = null!;
    private WebApplication app = null!;
    private Uri address = null!;
    private bool overridePeer;
    private CallPeer? peer;
    public async Task InitializeAsync()
    {
        var topics = new TopicSet(); db = AppDatabase.Open(file.Path, TimeProvider.System, topics);
        var calls = new SocketDispatcher(db.Writer, topics, TimeProvider.System, NullLogger.Instance);
        new WorkspacesFeature(new WorkspaceStore(db, TimeProvider.System)).Register(new WorkspacePeerGate(calls, this));
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0)); app = builder.Build(); app.UseWebSockets();
        var main = new SocketEndpoint("workspace-test", calls, "actual-source", TimeProvider.System);
        var bulk = new SocketEndpoint("workspace-test", calls, "actual-source", TimeProvider.System, bulk: true);
        app.Map("/socket", main.HandleAsync); app.Map("/socket/bulk", bulk.HandleAsync);
        app.Lifetime.ApplicationStopping.Register(main.CloseAll); app.Lifetime.ApplicationStopping.Register(bulk.CloseAll);
        await app.StartAsync();
        var http = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        address = new Uri($"ws://{http.Authority}/socket");
    }
    public async Task DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); db.Dispose(); file.Dispose(); }

    [Fact]
    public async Task Save_many_preserves_input_order_and_rolls_back_every_key_audit_and_publication_on_later_failure()
    {
        using var main = await Open(false); using var bulk = await Open(true);
        var first = BatchItem("first", 0); var second = BatchItem("second", 0);
        var initial = await Call(bulk, 1, "save", first);
        Assert.True(initial.GetProperty("t").GetString() == "result", initial.GetRawText());
        Assert.Equal(1, initial.GetProperty("result").GetProperty("revision").GetInt64());
        await Send(main, "{\"t\":\"sub\",\"id\":8,\"topic\":\"workspaces\",\"key\":\"actual-source/first//checkout\"}");
        Assert.Equal(1, Assert.Single((await Read(main)).GetProperty("items").EnumerateArray()).GetProperty("item").GetProperty("revision").GetInt64());
        await Send(main, "{\"t\":\"sub\",\"id\":9,\"topic\":\"workspaces\",\"key\":\"actual-source/second//checkout\"}");
        Assert.Empty((await Read(main)).GetProperty("items").EnumerateArray());
        var beforeAudit = db.QueryFirst("SELECT COUNT(*) FROM audit", row => row.Long(0), default);
        var conflict = new WorkspaceSaveManyArgs([first with { ExpectedRevision = 1 }, second with { ExpectedRevision = 1 }]);
        Assert.Equal("failed", (await Call(bulk, 2, "save_many", conflict)).GetProperty("code").GetString());
        Assert.Equal(1, db.QueryFirst("SELECT revision FROM workspace_snapshot WHERE session_id='first'", row => row.Long(0), default));
        Assert.Equal(beforeAudit, db.QueryFirst("SELECT COUNT(*) FROM audit", row => row.Long(0), default));
        db.Writer.Write("test.fail-batch", scope => scope.Execute("""
            CREATE TRIGGER fail_second_workspace BEFORE INSERT ON workspace_snapshot WHEN NEW.session_id='second'
            BEGIN SELECT RAISE(ABORT,'injected'); END
            """));
        beforeAudit = db.QueryFirst("SELECT COUNT(*) FROM audit", row => row.Long(0), default);
        var valid = new WorkspaceSaveManyArgs([first with { ExpectedRevision = 1 }, second]);
        Assert.Equal("failed", (await Call(bulk, 3, "save_many", valid)).GetProperty("code").GetString());
        Assert.Equal(beforeAudit, db.QueryFirst("SELECT COUNT(*) FROM audit", row => row.Long(0), default));
        Assert.Equal(1, db.QueryFirst("SELECT COUNT(*) FROM workspace_snapshot", row => row.Long(0), default));
        Assert.Equal(1, db.QueryFirst("SELECT revision FROM workspace_snapshot WHERE session_id='first'", row => row.Long(0), default));
        db.Writer.Write("test.restore-batch", scope => scope.Execute("DROP TRIGGER fail_second_workspace"));
        // Input order differs from subscription order; both invalidations must describe this whole successful commit.
        var saved = (await Call(bulk, 4, "save_many", new WorkspaceSaveManyArgs([second, first with { ExpectedRevision = 1 }]))).GetProperty("result").GetProperty("saved");
        Assert.Equal(new[] { "second", "first" }, saved.EnumerateArray().Select(item => item.GetProperty("sessionId").GetString()));
        Assert.Equal(new long[] { 1, 2 }, saved.EnumerateArray().Select(item => item.GetProperty("revision").GetInt64()));
        foreach (var item in saved.EnumerateArray())
        { Assert.Equal("actual-source", item.GetProperty("machine").GetString()); Assert.Equal("/checkout", item.GetProperty("checkoutRoot").GetString()); }
        var firstNotice = await Read(main);
        Assert.Equal(2, db.QueryFirst("SELECT COUNT(*) FROM workspace_snapshot", row => row.Long(0), default));
        var published = new[] { firstNotice, await Read(main) };
        Assert.Equal(new long[] { 1, 2 }, published.Select(frame => frame.GetProperty("item").GetProperty("revision").GetInt64()));
        Assert.Equal(2, db.QueryFirst("SELECT COUNT(*) FROM workspace_snapshot", row => row.Long(0), default));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"saves\":null}")]
    [InlineData("{\"saves\":[]}")]
    [InlineData("{\"saves\":[null]}")]
    public async Task Save_many_requires_a_present_nonempty_list_of_nonnull_items_before_effects(string args)
    {
        using var bulk = await Open(true);
        var audit = db.QueryFirst("SELECT COUNT(*) FROM audit", row => row.Long(0), default);
        await Send(bulk, $$"""{"t":"call","id":1,"feature":"workspaces","method":"save_many","args":{{args}}}""");
        Assert.Equal("bad_args", (await Read(bulk)).GetProperty("code").GetString());
        Assert.Equal(0, db.QueryFirst("SELECT COUNT(*) FROM workspace_snapshot", row => row.Long(0), default));
        Assert.Equal(audit, db.QueryFirst("SELECT COUNT(*) FROM audit", row => row.Long(0), default));
    }

    [Fact]
    public async Task Save_many_rejects_duplicate_exact_keys_and_remains_owner_only_and_bulk_only()
    {
        using var main = await Open(false); using var bulk = await Open(true);
        var save = BatchItem("same", 0); var one = new WorkspaceSaveManyArgs([save]);
        Assert.Equal("not_found", (await Call(main, 1, "save_many", one)).GetProperty("code").GetString());
        overridePeer = true;
        foreach (var denied in new CallPeer?[] { new PhonePeer("actual-device"), null })
        {
            peer = denied;
            Assert.Equal("forbidden", (await Call(bulk, 2, "save_many", one)).GetProperty("code").GetString());
        }
        overridePeer = false;
        var audit = db.QueryFirst("SELECT COUNT(*) FROM audit", row => row.Long(0), default);
        Assert.Equal("bad_args", (await Call(bulk, 3, "save_many", new WorkspaceSaveManyArgs([save, save]))).GetProperty("code").GetString());
        Assert.Equal(audit, db.QueryFirst("SELECT COUNT(*) FROM audit", row => row.Long(0), default));
        var result = (await Call(bulk, 4, "save_many", new WorkspaceSaveManyArgs([save, save with { CheckoutRoot = "/other" }, save with { Machine = "other-source" }]))).GetProperty("result");
        Assert.Equal(3, result.GetProperty("saved").GetArrayLength());
    }

    [Fact]
    public async Task Save_many_counts_the_entire_generated_args_and_refuses_escaped_unicode_before_effects()
    {
        using var bulk = await Open(true);
        var empty = new WorkspaceSaveManyArgs([BatchItem("boundary", 0) with { Snapshot = JsonSerializer.SerializeToElement("") }]);
        var overhead = JsonSerializer.SerializeToUtf8Bytes(empty, WorkspacesJson.Default.WorkspaceSaveManyArgs).Length;
        var text = new string('a', WorkspaceStore.MaxSnapshotBytes - overhead);
        var exact = new WorkspaceSaveManyArgs([empty.Saves[0] with { Snapshot = JsonSerializer.SerializeToElement(text) }]);
        Assert.Equal(WorkspaceStore.MaxSnapshotBytes, JsonSerializer.SerializeToUtf8Bytes(exact, WorkspacesJson.Default.WorkspaceSaveManyArgs).Length);
        Assert.Equal("result", (await Call(bulk, 1, "save_many", exact)).GetProperty("t").GetString());
        var audit = db.QueryFirst("SELECT COUNT(*) FROM audit", row => row.Long(0), default);
        var over = new WorkspaceSaveManyArgs([empty.Saves[0] with { ExpectedRevision = 1, Snapshot = JsonSerializer.SerializeToElement(text + "x") }]);
        Assert.Equal("failed", (await Call(bulk, 2, "save_many", over)).GetProperty("code").GetString());
        // The native wire JSON fits, but this batch's canonical default-encoder representation exceeds its budget.
        var unicode = new string('é', WorkspaceStore.MaxSnapshotBytes / 4);
        await Send(bulk, "{\"t\":\"call\",\"id\":3,\"feature\":\"workspaces\",\"method\":\"save_many\",\"args\":{\"saves\":[{\"machine\":\"actual-source\",\"sessionId\":\"unicode\",\"checkoutRoot\":\"/checkout\",\"expectedRevision\":0,\"snapshot\":\"" + unicode + "\"}]}}");
        Assert.Equal("failed", (await Read(bulk)).GetProperty("code").GetString());
        Assert.Equal(audit, db.QueryFirst("SELECT COUNT(*) FROM audit", row => row.Long(0), default));
        Assert.Equal(1, db.QueryFirst("SELECT COUNT(*) FROM workspace_snapshot", row => row.Long(0), default));
        var read = (await Call(bulk, 4, "read", new { machine = "actual-source", sessionId = "boundary", checkoutRoot = "/checkout" })).GetProperty("result");
        Assert.Equal(1, read.GetProperty("revision").GetInt64()); Assert.Equal(text, read.GetProperty("snapshot").GetString());
    }

    private static WorkspaceSaveArgs BatchItem(string session, long revision) => new("actual-source", session, "/checkout", revision,
        JsonSerializer.SerializeToElement(new { version = 1, files = new[] { new { path = "a", draft = "kept draft" } } }));

    [Fact]
    public async Task Read_and_save_are_bulk_only_and_named_revision_reaches_the_main_subscription_after_commit()
    {
        using var main = await Open(false); using var bulk = await Open(true);
        var key = new { machine = "actual-source", sessionId = "s", checkoutRoot = "/checkout" };
        var rejected = await Call(main, 1, "read", key);
        Assert.Equal("not_found", rejected.GetProperty("code").GetString());
        var save = new { key.machine, key.sessionId, key.checkoutRoot, expectedRevision = 0, snapshot = new { version = 1, activePath = "é file" } };
        Assert.Equal("not_found", (await Call(main, 2, "save", save)).GetProperty("code").GetString());
        var initial = (await Call(bulk, 3, "read", key)).GetProperty("result");
        Assert.Equal(0, initial.GetProperty("revision").GetInt64()); Assert.Equal(JsonValueKind.Null, initial.GetProperty("snapshot").ValueKind);
        await Send(main, "{\"t\":\"sub\",\"id\":9,\"topic\":\"workspaces\",\"key\":\"actual-source/s//checkout\"}");
        Assert.Empty((await Read(main)).GetProperty("items").EnumerateArray());
        Assert.Equal(1, (await Call(bulk, 4, "save", save)).GetProperty("result").GetProperty("revision").GetInt64());
        var changed = await Read(main);
        Assert.Equal("actual-source/s//checkout", changed.GetProperty("key").GetString());
        Assert.Equal(1, changed.GetProperty("item").GetProperty("revision").GetInt64());
        var restored = (await Call(bulk, 5, "read", key)).GetProperty("result");
        Assert.Equal("é file", restored.GetProperty("snapshot").GetProperty("activePath").GetString());
        Assert.Equal(1, restored.GetProperty("revision").GetInt64());
        Assert.Equal("failed", (await Call(bulk, 6, "save", save)).GetProperty("code").GetString());
        // The next successful commit is a publication barrier; the failed stale write must publish nothing.
        var second = new { key.machine, key.sessionId, key.checkoutRoot, expectedRevision = 1, snapshot = new { version = 1, activePath = "new" } };
        Assert.Equal(2, (await Call(bulk, 7, "save", second)).GetProperty("result").GetProperty("revision").GetInt64());
        Assert.Equal(2, (await Read(main)).GetProperty("item").GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task A_snapshot_at_the_utf8_limit_can_be_read_back_through_the_bulk_envelope()
    {
        using var bulk = await Open(true);
        var text = new string('é', (WorkspaceStore.MaxSnapshotBytes - 2) / 2);
        // Send actual UTF-8 JSON, as the native client does, without expanding non-ASCII into escape sequences.
        await Send(bulk, "{\"t\":\"call\",\"id\":1,\"feature\":\"workspaces\",\"method\":\"save\",\"args\":{\"machine\":\"actual-source\",\"sessionId\":\"s\",\"checkoutRoot\":\"/checkout\",\"expectedRevision\":0,\"snapshot\":\"" + text + "\"}}");
        var saved = await Read(bulk);
        Assert.Equal("result", saved.GetProperty("t").GetString());
        Assert.Equal(1, saved.GetProperty("result").GetProperty("revision").GetInt64());
        var read = await Call(bulk, 2, "read", new { machine = "actual-source", sessionId = "s", checkoutRoot = "/checkout" });
        Assert.Equal("result", read.GetProperty("t").GetString());
        Assert.Equal(text, read.GetProperty("result").GetProperty("snapshot").GetString());
    }

    [Fact]
    public async Task Phone_and_unknown_peers_cannot_read_write_or_subscribe_to_desktop_snapshots()
    {
        using var main = await Open(false); using var bulk = await Open(true);
        var key = new { machine = "actual-source", sessionId = "s", checkoutRoot = "/checkout" };
        var save = new { key.machine, key.sessionId, key.checkoutRoot, expectedRevision = 0, snapshot = new { version = 1, files = new[] { "private draft" } } };
        Assert.Equal(1, (await Call(bulk, 1, "save", save)).GetProperty("result").GetProperty("revision").GetInt64());
        overridePeer = true;
        foreach (var denied in new CallPeer?[] { new PhonePeer("actual-phone-device"), null })
        {
            peer = denied;
            Assert.Equal("forbidden", (await Call(bulk, 2, "read", key)).GetProperty("code").GetString());
            Assert.Equal("forbidden", (await Call(bulk, 3, "save", save)).GetProperty("code").GetString());
            await Send(main, "{\"t\":\"sub\",\"id\":4,\"topic\":\"workspaces\",\"key\":\"actual-source/s//checkout\"}");
            Assert.Equal("failed", (await Read(main)).GetProperty("code").GetString());
            await Send(main, "{\"t\":\"sub\",\"id\":5,\"topic\":\"workspaces\"}");
            Assert.Equal("failed", (await Read(main)).GetProperty("code").GetString());
        }
        overridePeer = false;
        var retained = (await Call(bulk, 6, "read", key)).GetProperty("result");
        Assert.Equal(1, retained.GetProperty("revision").GetInt64());
        Assert.Equal("private draft", retained.GetProperty("snapshot").GetProperty("files")[0].GetString());
    }

    [Fact]
    public async Task Two_windows_saving_the_same_revision_commit_one_snapshot_and_one_invalidation()
    {
        using var main = await Open(false); using var first = await Open(true); using var second = await Open(true);
        var key = new { machine = "actual-source", sessionId = "s", checkoutRoot = "/checkout" };
        await Send(main, "{\"t\":\"sub\",\"id\":9,\"topic\":\"workspaces\",\"key\":\"actual-source/s//checkout\"}");
        Assert.Empty((await Read(main)).GetProperty("items").EnumerateArray());
        object Save(string path, int revision) => new { key.machine, key.sessionId, key.checkoutRoot, expectedRevision = revision, snapshot = new { version = 1, activePath = path } };
        var replies = await Task.WhenAll(Call(first, 1, "save", Save("first", 0)), Call(second, 2, "save", Save("second", 0)));
        var winner = Assert.Single(replies, reply => reply.GetProperty("t").GetString() == "result");
        Assert.Equal(1, winner.GetProperty("result").GetProperty("revision").GetInt64());
        Assert.Equal("failed", Assert.Single(replies, reply => reply.GetProperty("t").GetString() == "error").GetProperty("code").GetString());
        Assert.Equal(1, (await Read(main)).GetProperty("item").GetProperty("revision").GetInt64());
        var retained = (await Call(first, 3, "read", key)).GetProperty("result");
        Assert.Equal(winner.GetProperty("id").GetInt32() == 1 ? "first" : "second", retained.GetProperty("snapshot").GetProperty("activePath").GetString());
        Assert.Equal(2, (await Call(first, 4, "save", Save("barrier", 1))).GetProperty("result").GetProperty("revision").GetInt64());
        Assert.Equal(2, (await Read(main)).GetProperty("item").GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task Real_desktop_registers_bulk_workspace_calls_and_restores_the_committed_snapshot_after_restart()
    {
        var home = Directory.CreateTempSubdirectory("assembly-workspace-desktop-").FullName;
        var launcher = new FakeLauncher();
        var key = new { machine = "workbox-test", sessionId = "workspace-only", checkoutRoot = "/remote/checkout" };
        var save = new { key.machine, key.sessionId, key.checkoutRoot, expectedRevision = 0, snapshot = new { version = 1, files = new[] { new { path = "a", draft = "exact é\r\n" } } } };
        try
        {
            await using (var backend = await DesktopBackend.StartAsync(home, "workspace-test", 0, launcher, TextWriter.Null))
            {
                var actual = new Uri($"ws://{backend.Address.Authority}/socket");
                using var main = await Open(actual, false); using var bulk = await Open(actual, true);
                Assert.Equal("not_found", (await Call(main, 1, "read", key)).GetProperty("code").GetString());
                await Send(main, "{\"t\":\"sub\",\"id\":9,\"topic\":\"workspaces\",\"key\":\"workbox-test/workspace-only//remote/checkout\"}");
                Assert.Empty((await Read(main)).GetProperty("items").EnumerateArray());
                Assert.Equal(1, (await Call(bulk, 2, "save", save)).GetProperty("result").GetProperty("revision").GetInt64());
                Assert.Equal(1, (await Read(main)).GetProperty("item").GetProperty("revision").GetInt64());
                var committed = (await Call(bulk, 3, "read", key)).GetProperty("result");
                Assert.Equal("exact é\r\n", committed.GetProperty("snapshot").GetProperty("files")[0].GetProperty("draft").GetString());
                Assert.Equal("failed", (await Call(bulk, 4, "save", save)).GetProperty("code").GetString());
            }
            await using var reopened = await DesktopBackend.StartAsync(home, "workspace-test", 0, launcher, TextWriter.Null);
            using var socket = await Open(new Uri($"ws://{reopened.Address.Authority}/socket"), true);
            var restored = (await Call(socket, 5, "read", key)).GetProperty("result");
            Assert.Equal(1, restored.GetProperty("revision").GetInt64());
            Assert.Equal("exact é\r\n", restored.GetProperty("snapshot").GetProperty("files")[0].GetProperty("draft").GetString());
            Assert.Equal(0, launcher.Launches);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    private Task<ClientWebSocket> Open(bool bulk) => Open(address, bulk);
    private static async Task<ClientWebSocket> Open(Uri address, bool bulk)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(bulk ? new Uri(address + "/bulk") : address, CancellationToken.None);
        await Send(socket, "{\"t\":\"hello\",\"token\":\"workspace-test\",\"protocol\":1}");
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString()); return socket;
    }
    private static Task<JsonElement> Call(WebSocket socket, int id, string method, WorkspaceSaveArgs args) =>
        Call(socket, id, method, JsonSerializer.SerializeToElement(args, WorkspacesJson.Default.WorkspaceSaveArgs));
    private static Task<JsonElement> Call(WebSocket socket, int id, string method, WorkspaceSaveManyArgs args) =>
        Call(socket, id, method, JsonSerializer.SerializeToElement(args, WorkspacesJson.Default.WorkspaceSaveManyArgs));
    private static async Task<JsonElement> Call(WebSocket socket, int id, string method, object args)
    {
        await Send(socket, JsonSerializer.Serialize(new { t = "call", id, feature = "workspaces", method, args }));
        return await Read(socket);
    }
    private static Task Send(WebSocket socket, string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Read(WebSocket socket)
    {
        using var message = new MemoryStream(); var bytes = new byte[64 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); WebSocketReceiveResult received;
        do { received = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), timeout.Token); message.Write(bytes, 0, received.Count); }
        while (!received.EndOfMessage);
        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
        using var json = JsonDocument.Parse(message.ToArray()); return json.RootElement.Clone();
    }

    private sealed class WorkspacePeerGate(ICallRegistry inner, WorkspacesSocketTests fixture) : ICallRegistry
    {
        private CallContext Context(CallContext context) => fixture.overridePeer ? context with { Peer = fixture.peer } : context;
        public void Read<A, R>(string feature, string method, JsonTypeInfo<A> args, JsonTypeInfo<R> result, Func<A, CallContext, Task<R>> handler, bool bulk = false) =>
            inner.Read(feature, method, args, result, (a, c) => handler(a, Context(c)), bulk);
        public void Write<A, R>(string feature, string method, JsonTypeInfo<A> args, JsonTypeInfo<R> result, Func<A, CallContext, Task<Func<IWriteScope, R>>> handler,
            Func<A, string?>? order = null, Action<A, CallContext>? failed = null, bool bulk = false) =>
            inner.Write(feature, method, args, result, (a, c) => handler(a, Context(c)), order, failed, bulk);
        public void Save<V>(string feature, JsonTypeInfo<V> value, Action<string, V, IWriteScope> save) => inner.Save(feature, value, save);
        public void Closed(Action<string> connection) => inner.Closed(connection);
        public void Topic<I>(string topic, JsonTypeInfo<I> item, Func<I, string> key, Func<string?, CallContext, IReadOnlyList<I>> state, bool terminal = false) =>
            inner.Topic(topic, item, key, (k, c) => state(k, Context(c)), terminal);
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state, Action<string, CallContext>? opened = null) => inner.Rows(topic, state, opened);
    }
}
