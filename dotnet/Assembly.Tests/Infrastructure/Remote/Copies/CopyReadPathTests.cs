using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Rows.Copies;
using Assembly.Application.Features.Chat.Sessions.Drafts;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Chat;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Tests.Draft.Base.Chat;

namespace Assembly.Tests.Infrastructure.Remote.Copies;

/// <summary>
/// A remote session saved here opens and scrolls from its copy, connected or not (TSK-1692). The desktop answers
/// <c>sessions/open_target</c> and the rows reads over its own socket; the remote logs every rows and open call it answers.
/// </summary>
public sealed class CopyReadPathTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly TwoHosts _hosts = new(forwardCommands: true);
    private RowStore _remoteRows = null!;
    private LiveHub? _remoteHub;
    private ICallRegistry _macCalls = null!;
    // Every rows and open call the remote answered, as "feature/method".
    private readonly ConcurrentQueue<string> _calls = new();
    // Each session whose download reached the bottom: the remote answered it with no rows.
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _bottomed = new();
    // Released to let a held backfill chunk through.
    private TaskCompletionSource? _backfill;
    private ClientWebSocket _socket = null!;
    // Page and subagent reads go on the bulk socket.
    private ClientWebSocket _bulk = null!;
    private long _id;

    public async Task InitializeAsync()
    {
        _hosts.Remote.AdditionalFeatures = calls =>
        {
            _remoteHub?.Dispose();
            _remoteRows = RowStore.Open(_hosts.Remote.Fixture.Db);
            _remoteHub = new LiveHub(_remoteRows, topics: _hosts.Remote.Topics);
            var machines = new MachineSet(new FakeMachine(null, _hosts.Remote.Sessions));
            var logged = new Logged(calls, this);
            new RowsFeature(_remoteHub, _remoteRows, _hosts.Remote.Sessions, machines, new DiskPathProbe(), Path.GetTempPath()).Register(logged);
            new SessionOpenCalls(_hosts.Remote.Fixture.Db, _hosts.Remote.Runtime, _hosts.Remote.Sessions, machines).Register(logged);
        };
        _hosts.Mac.AdditionalFeatures = calls => _macCalls = calls;
        await _hosts.Start();
        // The remote's own record of its engine session, which its open and rows reads look up.
        _hosts.Remote.Sessions.Add("s", "codex", "/test", null!);
        await _hosts.StartMirror();
        // The desktop's open and rows reads, as the app wires them.
        var db = _hosts.Mac.Fixture.Db;
        new SessionOpenCalls(db, _hosts.Mac.Runtime, _hosts.Mac.Sessions, _hosts.Machines, _hosts.Connections).Register(_macCalls);
        new RowsFeature(_hosts.MacHub, RowStore.Open(db), _hosts.Mac.Sessions, _hosts.Machines, new DiskPathProbe(), Path.GetTempPath(),
            Sources()).Register(_macCalls);
        _socket = await OpenSocket(bulk: false);
        _bulk = await OpenSocket(bulk: true);
    }

    // Pins: spec test 1.
    [Fact]
    public async Task Spec01_a_never_saved_session_shows_its_newest_page_before_the_download_ends()
    {
        Seed("s", 250);
        _backfill = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var read = Result(await Open(Target("historical")));
        var page = await Page(read, "s");

        Assert.Equal(250, page.Seqs.Max());
        Assert.True(page.HasBefore);
        Assert.False(_hosts.MacHub.Copies.RemoteCopyOf("s")!.Complete);
        _backfill.TrySetResult();
        await DownloadedAsync("s");
    }

    // Pins: spec test 9. A copied engine session also asks its machine for the draft once (controller decision 1).
    [Fact]
    public async Task Spec09_reopening_and_scrolling_a_complete_session_sends_no_rows_call_and_no_open_target()
    {
        Seed("s", 250);
        Result(await Open(Target("historical")));
        await DownloadedAsync("s");
        _calls.Clear();

        var read = Result(await Open(Target("historical")));
        var newest = await Page(read, "s");
        var older = await Page(read, "s", newest.Before);
        await Read("subagents", read, "s");
        await Read("files", read, "s");
        await Read("tokens", read, "s");
        await Read("attachments", read, "s");

        Assert.True(older.Seqs.Max() < newest.Seqs.Min());
        Assert.DoesNotContain(_calls, call => call.StartsWith("rows/", StringComparison.Ordinal));
        Assert.DoesNotContain("sessions/open_target", _calls);

        Result(await Open(Target("engine")));
        Assert.Single(_calls, call => call == "sessions/open_target");
        Assert.DoesNotContain(_calls, call => call.StartsWith("rows/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rows_newer_than_the_copy_arrive_through_the_subscription_at_reopen_with_no_rows_read()
    {
        Seed("s", 250);
        Result(await Open(Target("historical")));
        await DownloadedAsync("s");
        _calls.Clear();
        // Saved on the remote since the copy was last current.
        for (var seq = 251; seq <= 255; seq++)
            Assert.True(_remoteHub!.PublishCopy(new StoredRow("s", seq, "run", $"item-{seq}", RowKind.Tool,
                JsonSerializer.SerializeToElement(new { text = $"row {seq}" }), seq, PrevSeq: seq - 1)));
        using var viewer = _hosts.MacHub.Subscribe("s", 250);

        var read = Result(await Open(Target("historical")));
        await Page(read, "s");
        for (var seq = 251; seq <= 255; seq++)
            Assert.Equal(seq, (await viewer.Queue.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Seq);

        Assert.Equal(255, (await Page(read, "s")).Seqs.Max());
        Assert.DoesNotContain(_calls, call => call.StartsWith("rows/", StringComparison.Ordinal));
    }

    // Pins: spec test 11.
    [Fact]
    public async Task Spec11_offline_a_complete_session_opens_and_scrolls_with_subagents_files_tokens_and_attachments()
    {
        Seed("s", 250);
        Card("s", 251, "child-a", "native-a");
        Result(await Open(Target("historical")));
        await DownloadedAsync("s");
        await _hosts.DisconnectMirror();

        var read = Result(await Open(Target("historical")));
        var newest = await Page(read, "s");
        var older = await Page(read, "s", newest.Before);
        var subagents = await Read("subagents", read, "s");
        await Read("files", read, "s");
        await Read("tokens", read, "s");
        var attachments = await Read("attachments", read, "s");

        Assert.Equal(251, newest.Seqs.Max());
        Assert.True(older.Seqs.Max() < newest.Seqs.Min());
        Assert.Single(subagents.GetProperty("items").EnumerateArray());
        Assert.Equal("workbox-test", attachments.GetProperty("machine").GetString());
    }

    // Pins: spec test 12.
    [Fact]
    public async Task Spec12_offline_a_never_saved_session_fails_with_not_saved()
    {
        Seed("s", 20);
        await _hosts.DisconnectMirror();

        var error = Error(await Open(Target("historical")), "not_saved");

        Assert.Equal("Not saved on this machine. Connect workbox-test to open it.", error.GetProperty("message").GetString());
        Assert.Null(_hosts.MacHub.Copies.RemoteCopyOf("s"));
    }

    // Pins: spec test 13: the copy is keyed by the engine, not the mirror's generation.
    [Fact]
    public async Task Spec13_a_generation_change_keeps_the_copy_and_downloads_nothing()
    {
        Seed("s", 250);
        Result(await Open(Target("historical")));
        await DownloadedAsync("s");
        var before = MacRows("s");
        await _hosts.DisconnectMirror();
        // As a mirror reset leaves it: the same engine under a newer generation.
        Mac("UPDATE engine_mirror SET generation=generation+1");
        _calls.Clear();

        var read = Result(await Open(Target("historical")));
        var page = await Page(read, "s");

        Assert.Equal(_hosts.Mac.Fixture.Number("SELECT generation FROM engine_mirror"), read.Generation);
        Assert.Equal(250, page.Seqs.Max());
        Assert.Equal(before, MacRows("s"));
        Assert.Empty(_calls);
    }

    // Pins: spec test 19.
    [Fact]
    public async Task Spec19_a_native_subagent_card_opens_its_child_from_the_copy()
    {
        Seed("s", 150);
        Card("s", 151, "child-a", "native-a");
        Seed("child-a", 10);
        Result(await Open(Target("historical")));
        await DownloadedAsync("s");
        await _hosts.ConnectedMachine.EnsureCopyAsync("child-a", default).WaitAsync(Wait);
        await DownloadedAsync("child-a");
        await _hosts.DisconnectMirror();

        var machine = _hosts.Remote.Fixture.Machine;
        var read = Result(await Open(new("native", machine, "s", "subagent:native-a", 151, "child-a", new(machine, "s"), 9)));
        var page = await Page(read, "child-a");

        Assert.Equal(("child-a", "s", true), (read.SessionId, read.ParentSessionId, read.ReadOnly));
        Assert.Equal(Enumerable.Range(1, 10).Select(seq => (long)seq), page.Seqs.Order());
    }

    [Fact]
    public async Task A_forwarded_file_call_still_needs_a_live_source()
    {
        Seed("s", 20);
        var read = Result(await Open(Target("historical")));
        await DownloadedAsync("s");
        await _hosts.DisconnectMirror();

        using var copy = await Sources().CaptureAsync(read.ExpectedSource, "s", default);
        copy.EnsureCurrent();
        var live = await Assert.ThrowsAsync<CallFailed>(() => Sources().CaptureLiveAsync(read.ExpectedSource, "s", default));

        Assert.Equal("source_changed", live.Reason);
    }

    [Fact]
    public async Task A_copy_under_a_retired_mirror_is_not_saved()
    {
        Seed("s", 20);
        var read = Result(await Open(Target("historical")));
        await DownloadedAsync("s");
        await _hosts.DisconnectMirror();
        Mac("UPDATE engine_mirror SET state='retired'");

        Error(await Open(Target("historical")), "not_saved");
        Error(await Call("rows", "page", Args(read, "s")), "not_saved");
    }

    // Controller decision 1 at checkpoint 1.
    [Fact]
    public async Task A_copied_remote_session_opens_with_its_remote_draft_when_connected_and_empty_offline()
    {
        Seed("s", 20);
        _hosts.Remote.Fixture.Db.Writer.Write("test.draft", scope =>
        {
            DraftStore.Set(scope, "s", new SessionDraft("half-written", []), 1000);
            return 0;
        });
        Result(await Open(Target("engine")));
        await DownloadedAsync("s");

        var connected = Result(await Open(Target("engine")));
        await _hosts.DisconnectMirror();
        var offline = Result(await Open(Target("engine")));

        Assert.Equal("half-written", connected.Draft?.Text);
        Assert.Null(offline.Draft);
        Assert.False(offline.ReadOnly);
    }

    private SessionRowSources Sources() =>
        new(_hosts.Mac.Fixture.Db, _hosts.Mac.Sessions, _hosts.Machines, _hosts.Connections);

    private SessionOpenTarget Target(string kind) => new(kind, _hosts.Remote.Fixture.Machine, "s", RequestId: 7);

    // One download runs at a time, so a second session's first chunk is saved only once this one's download has ended.
    // A session within one chunk is complete with its first one.
    private async Task DownloadedAsync(string session)
    {
        if (_hosts.MacHub.Copies.RemoteCopyOf(session)?.Complete == true) return;
        await Bottomed(session).Task.WaitAsync(Wait);
        var other = $"after-{session}-{Guid.NewGuid():N}";
        Seed(other, 1);
        await _hosts.ConnectedMachine.EnsureCopyAsync(other, default).WaitAsync(Wait);
        Assert.True(_hosts.MacHub.Copies.RemoteCopyOf(session)?.Complete);
    }

    private TaskCompletionSource Bottomed(string session) =>
        _bottomed.GetOrAdd(session, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    private void Seed(string session, int count)
    {
        for (var seq = 1; seq <= count; seq++)
            Assert.True(_remoteRows.Copy(new StoredRow(session, seq, "run", $"item-{seq}", RowKind.Tool,
                JsonSerializer.SerializeToElement(new { text = $"row {seq}" }), seq, PrevSeq: seq - 1)));
    }

    // A native subagent card for child, whose provider session is native.
    private void Card(string session, long seq, string child, string native) =>
        Assert.True(_remoteRows.Copy(new StoredRow(session, seq, "run", $"{CopyRecords.SubagentItem}{native}", RowKind.Subagent,
            JsonSerializer.SerializeToElement(new { sessionId = child, title = child, state = "running" }), seq, PrevSeq: seq - 1)));

    private void Mac(string sql) => _hosts.Mac.Fixture.Db.Writer.Write("test.mac", scope => scope.Execute(sql));

    private IReadOnlyList<string> MacRows(string session) => _hosts.Mac.Fixture.Db.Query(
        "SELECT seq, item_id, kind, payload FROM rows WHERE session_id=$s ORDER BY seq",
        row => $"{row.Long(0)} {row.Text(1)} {row.Text(2)} {row.Text(3)}", default, new SqlArg("$s", session));

    private sealed record PageRows(long[] Seqs, bool HasBefore, long? Before);

    private async Task<PageRows> Page(SessionOpenTargetRead read, string session, long? before = null)
    {
        var args = Args(read, session);
        if (before is { } below) args["before"] = below;
        var page = Ok(await Call("rows", "page", args));
        return new([.. page.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("seq").GetInt64())],
            page.GetProperty("hasBefore").GetBoolean(),
            page.GetProperty("before").ValueKind == JsonValueKind.Null ? null : page.GetProperty("before").GetInt64());
    }

    private async Task<JsonElement> Read(string method, SessionOpenTargetRead read, string session) =>
        Ok(await Call("rows", method, Args(read, session)));

    private static JsonObject Args(SessionOpenTargetRead read, string session) => new()
    {
        ["session"] = session,
        ["expectedSource"] = JsonSerializer.SerializeToNode(read.ExpectedSource, SessionOpenJson.Default.ExpectedSource),
    };

    private Task<JsonElement> Open(SessionOpenTarget target) =>
        Call("sessions", "open_target", JsonSerializer.SerializeToNode(target, SessionOpenJson.Default.SessionOpenTarget)!);

    private static SessionOpenTargetRead Result(JsonElement reply) =>
        Ok(reply).Deserialize(SessionOpenJson.Default.SessionOpenTargetRead)!;

    private static JsonElement Ok(JsonElement reply)
    {
        Assert.True(reply.GetProperty("t").GetString() == "result", reply.GetRawText());
        return reply.GetProperty("result");
    }

    private static JsonElement Error(JsonElement reply, string code)
    {
        Assert.Equal("error", reply.GetProperty("t").GetString());
        Assert.Equal(code, reply.GetProperty("code").GetString());
        return reply;
    }

    private async Task<ClientWebSocket> OpenSocket(bool bulk)
    {
        var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri($"ws://{_hosts.Mac.Address.Authority}/socket{(bulk ? "/bulk" : "")}"), timeout.Token);
        await Send(socket, "{\"t\":\"hello\",\"token\":\"isolated-token\",\"protocol\":2}");
        Assert.Equal("welcome", (await Receive(socket)).GetProperty("t").GetString());
        return socket;
    }

    // One call on the desktop's socket, and its reply.
    private async Task<JsonElement> Call(string feature, string method, JsonNode args)
    {
        var id = Interlocked.Increment(ref _id);
        var frame = new JsonObject { ["t"] = "call", ["id"] = id, ["feature"] = feature, ["method"] = method, ["args"] = args };
        var socket = feature == "rows" && method is "page" or "subagents" ? _bulk : _socket;
        await Send(socket, frame.ToJsonString());
        return await Receive(socket);
    }

    private static Task Send(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> Receive(WebSocket socket)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[65536];
        using var timeout = new CancellationTokenSource(Wait);
        WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            bytes.Write(buffer, 0, part.Count);
        }
        while (!part.EndOfMessage);
        using var json = JsonDocument.Parse(bytes.ToArray());
        return json.RootElement.Clone();
    }

    public async Task DisposeAsync()
    {
        _backfill?.TrySetResult();
        _socket?.Dispose();
        _bulk?.Dispose();
        await _hosts.DisposeAsync();
        _remoteHub?.Dispose();
    }

    // The remote's rows and open calls, logged; a backfill chunk waits while one is held.
    private sealed class Logged(ICallRegistry inner, CopyReadPathTests test) : ICallRegistry
    {
        public void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
            Func<TArgs, CallContext, Task<TResult>> handler, bool bulk = false) =>
            inner.Read(feature, method, args, result, async (value, call) =>
            {
                test._calls.Enqueue($"{feature}/{method}");
                if (value is RowsFeature.BeforeArgs { Before: not null } && test._backfill is { } held)
                    await held.Task.WaitAsync(call.Cancellation);
                var answer = await handler(value, call);
                if (value is RowsFeature.BeforeArgs asked && answer is BeforeChunk { Rows.Count: 0 })
                    test.Bottomed(asked.Session).TrySetResult();
                return answer;
            }, bulk);

        public void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
            Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler, Func<TArgs, string?>? order = null,
            Action<TArgs, CallContext>? failed = null, bool bulk = false) => inner.Write(feature, method, args, result, handler, order, failed, bulk);

        public void Save<T>(string feature, JsonTypeInfo<T> type, Action<string, T, IWriteScope> save) => inner.Save(feature, type, save);

        public void Closed(Action<string> connection) => inner.Closed(connection);

        public void Topic<T>(string topic, JsonTypeInfo<T> type, Func<T, string> key,
            Func<string?, CallContext, IReadOnlyList<T>> state, bool terminal = false) => inner.Topic(topic, type, key, state, terminal);

        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state,
            Action<string, CallContext>? opened = null) => inner.Rows(topic, state, opened);
    }
}
