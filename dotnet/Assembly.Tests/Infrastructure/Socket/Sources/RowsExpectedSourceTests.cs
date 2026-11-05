using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Rows.Context;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Chat;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Engine;
using Assembly.Tests.Infrastructure.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed class RowsExpectedSourceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Expected_source_without_a_provider_refuses_watch_page_and_subagents()
    {
        var topics = new TopicSet();
        using var f = new EngineFixture(topics: topics);
        var sessions = SessionStore.Open(f.Db, f.Time, "checkouts");
        var session = sessions.Create("codex", "repo").SessionId; f.SeedSession(session);
        using var hub = new LiveHub(RowStore.Open(f.Db), topics: topics);
        var machines = new MachineSet(new FakeMachine(null, sessions));
        await using var host = await SocketHost.Start(f, topics, new RowsFeature(hub, RowStore.Open(f.Db), sessions, machines, new DiskPathProbe()));
        var expected = new ExpectedSource(f.Machine, 0, null);
        using var control = await host.Open();
        await Subscribe(control, session, expected);
        AssertRefused(await Read(control));
        using var bulk = await host.Open(bulk: true);
        foreach (var method in new[] { "page", "subagents" })
        {
            await Call(bulk, 1, method, session, expected);
            AssertRefused(await Read(bulk));
        }
    }

    [Fact]
    public async Task Local_zero_generation_null_route_works_and_wrong_identity_generation_or_route_refuses()
    {
        var topics = new TopicSet();
        using var f = new EngineFixture(topics: topics);
        var sessions = SessionStore.Open(f.Db, f.Time, "checkouts");
        var session = sessions.Create("codex", "repo").SessionId; f.SeedSession(session);
        var store = RowStore.Open(f.Db);
        using var hub = new LiveHub(store, topics: topics);
        Publish(hub, session, "LOCAL_OWNER");
        var machines = new MachineSet(new FakeMachine(null, sessions));
        await using var host = await SocketHost.Start(f, topics,
            new RowsFeature(hub, store, sessions, machines, new DiskPathProbe(), new SessionRowSources(f.Db, sessions, machines)));
        var expected = new ExpectedSource(f.Machine, 0, null);
        using var bulk = await host.Open(bulk: true);
        await Call(bulk, 1, "page", session, expected);
        var page = await Read(bulk); Assert.Equal("result", Type(page)); Assert.Contains("LOCAL_OWNER", page.GetRawText());
        await Call(bulk, 2, "subagents", session, expected);
        Assert.Equal("result", Type(await Read(bulk)));
        using var control = await host.Open();
        await Subscribe(control, session, expected);
        var replay = await ReplayThrough(control, store.LastSeq(session));
        Assert.Contains("LOCAL_OWNER", string.Join("", replay.Select(frame => frame.GetRawText())));
        await Send(control, new { t = "unsub", id = 1 });
        foreach (var wrong in new[] { expected with { Machine = "saved-name" }, expected with { Generation = 1 }, expected with { RouteMachine = "local" } })
        {
            await Subscribe(control, session, wrong);
            AssertRefused(await Read(control));
            await Call(bulk, 3, "page", session, wrong);
            AssertRefused(await Read(bulk));
            await Call(bulk, 4, "subagents", session, wrong);
            AssertRefused(await Read(bulk));
        }
    }

    [Fact]
    public async Task Remote_reads_use_actual_owner_with_local_marker_and_never_the_poisoned_display_copy()
    {
        await using var f = await RemoteFixture.Start();
        Child(f.OwnerHub, f.Session, "z-owner-child");
        var secondAtCut = RowStore.Open(f.Hosts.Remote.Fixture.Db).RowsAfter(f.Session, 0)
            .Single(row => row.ItemId == "subagent:z-owner-child");
        using var bulk = await f.Socket.Open(bulk: true);
        await Call(bulk, 1, "page", f.Session, f.Expected);
        var page = await Read(bulk); Assert.Equal("result", Type(page));
        Assert.Contains("OWNER_ONLY", page.GetRawText()); Assert.DoesNotContain("POISONED_COPY", page.GetRawText());
        await Send(bulk, new { t = "call", id = 2, feature = "rows", method = "subagents", args = new { session = f.Session, limit = 1, expectedSource = f.Expected } });
        var children = await Read(bulk); Assert.Equal("result", Type(children));
        var first = children.GetProperty("result").Deserialize(RowJsonContext.Rows.SubagentPage)!;
        Assert.Equal("subagent:owner-child", Assert.Single(first.Items).ItemId);
        Assert.NotNull(first.Next);
        Assert.Contains("owner-child", children.GetRawText()); Assert.DoesNotContain("poison-child", children.GetRawText());
        var changed = new RowBatch();
        changed.Rows.Add(new(null, "subagent:z-owner-child", RowKind.Subagent, JsonSerializer.SerializeToElement(
            new SubagentPayload("z-owner-child", "UPDATED_AFTER_FIRST_PAGE", null, "running", null, null, false, null), RowJsonContext.Rows.SubagentPayload), 1001));
        f.OwnerHub.Publish(f.Session, changed);
        await Send(bulk, new { t = "call", id = 3, feature = "rows", method = "subagents", args = new { session = f.Session, after = first.Next, limit = 1, expectedSource = f.Expected } });
        var continuation = await Read(bulk); Assert.Equal("result", Type(continuation));
        var second = continuation.GetProperty("result").Deserialize(RowJsonContext.Rows.SubagentPage)!;
        var original = Assert.Single(second.Items);
        Assert.Equal(secondAtCut.ItemId, original.ItemId); Assert.Equal(secondAtCut.Seq, original.Seq);
        Assert.Equal("z-owner-child", original.Title); Assert.Equal("idle", original.State);
        Assert.NotEqual(first.Items.Single().ItemId, original.ItemId); Assert.Null(second.Next);
        Assert.DoesNotContain("UPDATED_AFTER_FIRST_PAGE", continuation.GetRawText());
        Assert.DoesNotContain("poison-child", continuation.GetRawText());
        Assert.All(f.OwnerSources.Captures, source => Assert.Equal(new(f.Expected.Machine, 0, null), source));
        Assert.Equal(3, f.OwnerSources.Captures.Count);
        await Send(bulk, new { t = "call", id = 4, feature = "rows", method = "subagents", args = new { session = f.Session, after = first.Next, limit = 1, expectedSource = f.Expected with { Generation = f.Expected.Generation + 1 } } });
        var refused = await Read(bulk); AssertRefused(refused); Assert.DoesNotContain("poison", refused.GetRawText());
        Assert.Equal(3, f.OwnerSources.Captures.Count);
        Assert.All(f.Sources.Captures, source => Assert.Equal(f.Expected, source));
        using var control = await f.Socket.Open();
        await Subscribe(control, f.Session, f.Expected);
        var replay = await ReplayThrough(control, RowStore.Open(f.Hosts.Remote.Fixture.Db).LastSeq(f.Session));
        var wire = string.Join("", replay.Select(frame => frame.GetRawText()));
        Assert.Contains("OWNER_ONLY", wire); Assert.DoesNotContain("POISONED_COPY", wire);
        Assert.Equal(new(f.Expected.Machine, 0, null), f.OwnerSources.Captures.Last());
    }

    [Fact]
    public async Task Remote_tokens_answer_the_owners_total_and_never_the_poisoned_copy()
    {
        await using var f = await RemoteFixture.Start();
        Stats(f.OwnerHub, f.Session, 4_000);
        Stats(f.LocalHub, f.Session, 999_999);
        using var control = await f.Socket.Open();
        await Send(control, new { t = "call", id = 1, feature = "rows", method = "tokens", args = new { session = f.Session }, expectedSource = f.Expected });
        var answer = await Read(control);
        Assert.True(Type(answer) == "result", answer.GetRawText());
        Assert.Equal(4_000, answer.GetProperty("result").GetProperty("total").GetInt64());
        Assert.Equal(f.Expected, f.Sources.Captures.Single());
        Assert.Equal(new(f.Expected.Machine, 0, null), f.OwnerSources.Captures.Single());
    }

    // The named file exists only in the owner's folder, so a check on this side couldn't keep it.
    [Fact]
    public async Task Remote_files_answer_the_owners_list_unfiltered_and_never_the_poisoned_copy()
    {
        await using var f = await RemoteFixture.Start();
        var folder = Directory.CreateDirectory(f.Hosts.Remote.Sessions.Root(f.Hosts.Remote.Sessions.Get(f.Session)!)!);
        using var removed = new Removed(folder);
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, "owner-only.md"), "x");
        Tool(f.OwnerHub, f.Session, "t1", """{"name":"sh","state":"completed","hasDetail":false,"kind":"execute","named":["owner-only.md","owner-gone.md"]}""");
        Tool(f.OwnerHub, f.Session, "t2", """{"name":"edit","state":"completed","path":"owner-edit.rs","added":2,"hasDetail":false,"kind":"edit"}""");
        Tool(f.LocalHub, f.Session, "t3", """{"name":"edit","state":"completed","path":"poisoned.rs","hasDetail":false,"kind":"edit"}""");
        using var control = await f.Socket.Open();
        await Send(control, new { t = "call", id = 1, feature = "rows", method = "files", args = new { session = f.Session }, expectedSource = f.Expected });
        var answer = await Read(control);
        Assert.True(Type(answer) == "result", answer.GetRawText());
        var files = answer.GetProperty("result").EnumerateArray().ToArray();
        Assert.Equal(["owner-edit.rs", "owner-only.md"], files.Select(file => file.GetProperty("path").GetString()));
        Assert.Equal((2, 1), (files[0].GetProperty("added").GetInt64(), files[0].GetProperty("count").GetInt64()));
        Assert.Equal(f.Expected, f.Sources.Captures.Single());
        Assert.Equal(new(f.Expected.Machine, 0, null), f.OwnerSources.Captures.Single());
    }

    [Fact]
    public async Task Invalidation_before_capture_finishes_refuses_without_first_rows_or_reacquisition()
    {
        await using var f = await RemoteFixture.Start();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Sources.BeforeCapture = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        using var control = await f.Socket.Open();
        await Subscribe(control, f.Session, f.Expected);
        await entered.Task.WaitAsync(Wait);
        await f.Hosts.DisconnectMirror();
        release.TrySetResult();
        AssertRefused(await Read(control));
        Assert.Equal(1, f.Sources.Attempts);
        Assert.Empty(f.Sources.Captures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalidation_drops_already_queued_first_or_next_rows_before_the_real_socket_sender(bool next)
    {
        await using var f = await RemoteFixture.Start();
        using var control = await f.Socket.Open();
        if (next)
        {
            var through = RowStore.Open(f.Hosts.Remote.Fixture.Db).LastSeq(f.Session);
            await Subscribe(control, f.Session, f.Expected);
            await ReplayThrough(control, through);
            JsonElement forwarded;
            do { forwarded = await f.Sources.Enqueued.Reader.ReadAsync().AsTask().WaitAsync(Wait); }
            while (Type(forwarded) != "rows" || forwarded.GetProperty("row").GetProperty("seq").GetInt64() < through);
        }
        await f.Socket.Block(control);
        if (next) Publish(f.OwnerHub, f.Session, "MUST_NOT_ESCAPE");
        else await Subscribe(control, f.Session, f.Expected);
        var queued = await f.Sources.Enqueued.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        if (next) { Assert.Equal("rows", Type(queued)); Assert.Contains("MUST_NOT_ESCAPE", queued.GetRawText()); }
        else Assert.Equal("state", Type(queued));
        await f.Hosts.DisconnectMirror();
        f.Socket.Release();
        var frames = await Until(control, frame => Type(frame) == "error");
        AssertRefused(frames.Last());
        Assert.DoesNotContain(frames, frame => Type(frame) is "state" or "rows");
        Assert.DoesNotContain("MUST_NOT_ESCAPE", string.Join("", frames.Select(frame => frame.GetRawText())));
        Assert.Equal(1, f.Sources.Attempts);
        await f.Sources.Disposed.Reader.ReadAsync().AsTask().WaitAsync(Wait);
    }

    [Fact]
    public async Task Same_saved_name_restored_owner_invalidates_generation_without_remapping_the_old_subscription_or_cursor()
    {
        await using var f = await RemoteFixture.Start();
        var folder = Directory.CreateTempSubdirectory("rows-source-restore-").FullName;
        try
        {
            var backup = Path.Combine(folder, "owner.db");
            f.Hosts.Remote.Fixture.BackupTo(backup);
            f.Hosts.Remote.Commit(1);
            await f.Hosts.Reached(f.Hosts.Remote.Through);
            using var control = await f.Socket.Open();
            await Subscribe(control, f.Session, f.Expected);
            Assert.Equal("state", Type(await Read(control)));
            var old = f.Expected;
            var oldOwner = f.OwnerSources;
            var mirror = f.Hosts.Connections.CaptureMirrorOwners().Single().Mirror;
            var caught = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void CaughtUp() => caught.TrySetResult();
            mirror.CaughtUp += CaughtUp;
            try
            {
                // Actual isolated database restore, same machine id/address/name, lower owning watermark.
                // The normal mirror cursor refusal forces a new snapshot generation.
                await f.Hosts.Remote.Restore(backup);
                AssertRefused((await Until(control, frame => Type(frame) == "error")).Last());
                await caught.Task.WaitAsync(Wait);
            }
            finally { mirror.CaughtUp -= CaughtUp; }
            var current = f.CurrentSource();
            Assert.Equal(old.Machine, current.Machine); Assert.Equal(old.RouteMachine, current.RouteMachine);
            Assert.NotEqual(old.Generation, current.Generation);
            Assert.Equal(1, f.Sources.Attempts);
            using var bulk = await f.Socket.Open(bulk: true);
            await Call(bulk, 1, "subagents", f.Session, old, after: "original-owner-cursor");
            AssertRefused(await Read(bulk));
            Assert.Single(oldOwner.Captures); Assert.Empty(f.OwnerSources.Captures);
            await Call(bulk, 2, "page", f.Session, current);
            Assert.Equal("result", Type(await Read(bulk)));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsubscribe_or_socket_close_disposes_the_captured_proxy_and_owner_watch(bool close)
    {
        await using var f = await RemoteFixture.Start();
        var unwatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Unwatched(string session) { if (session == f.Session) unwatched.TrySetResult(); }
        f.OwnerHub.Unwatched += Unwatched;
        using var control = await f.Socket.Open();
        try
        {
            await Subscribe(control, f.Session, f.Expected);
            Assert.Equal("state", Type(await Read(control)));
            if (close) control.Abort();
            else
            {
                await Send(control, new { t = "unsub", id = 1 });
                await Send(control, new { t = "call", id = 2, feature = "probe", method = "echo", args = new { } });
                Assert.Equal("result", Type((await Until(control, frame => frame.TryGetProperty("id", out var id) && id.GetInt64() == 2)).Last()));
            }
            await f.Sources.Disposed.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            await unwatched.Task.WaitAsync(Wait);
            Assert.Equal(1, f.Sources.Attempts);
            Assert.False(f.OwnerHub.HasSubscribers(f.Session));
        }
        finally { f.OwnerHub.Unwatched -= Unwatched; }
    }

    private static void AssertRefused(JsonElement frame)
    {
        Assert.Equal("error", Type(frame)); Assert.Equal("source_changed", frame.GetProperty("code").GetString());
        Assert.False(frame.TryGetProperty("rows", out _)); Assert.False(frame.TryGetProperty("result", out _));
    }
    private static string? Type(JsonElement frame) => frame.GetProperty("t").GetString();
    private static Task Subscribe(WebSocket socket, string session, ExpectedSource expected) =>
        Send(socket, new { t = "sub", id = 1, topic = "rows", key = session, after = 0, expectedSource = expected });
    private static Task Call(WebSocket socket, int id, string method, string session, ExpectedSource expected, string? after = null) =>
        Send(socket, new { t = "call", id, feature = "rows", method, args = new { session, after, expectedSource = expected } });
    private static Task Send(WebSocket socket, object value) => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Read(WebSocket socket)
    {
        using var stop = new CancellationTokenSource(Wait);
        using var buffer = new MemoryStream(); var bytes = new byte[65536];
        WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), stop.Token);
            Assert.Equal(WebSocketMessageType.Text, part.MessageType); buffer.Write(bytes, 0, part.Count);
        } while (!part.EndOfMessage);
        using var json = JsonDocument.Parse(buffer.ToArray()); return json.RootElement.Clone();
    }
    private static async Task<List<JsonElement>> ReplayThrough(WebSocket socket, long through)
    {
        var state = await Read(socket); Assert.Equal("state", Type(state));
        var frames = new List<JsonElement> { state };
        var seq = state.GetProperty("seq").GetInt64();
        while (seq < through)
        {
            var frame = await Read(socket); Assert.Equal("rows", Type(frame));
            var row = frame.GetProperty("row");
            Assert.Equal(seq, row.GetProperty("prevSeq").GetInt64());
            var current = row.GetProperty("seq").GetInt64(); Assert.True(current > seq);
            seq = current; frames.Add(frame);
        }
        Assert.Equal(through, seq);
        return frames;
    }
    private static async Task<List<JsonElement>> Until(WebSocket socket, Func<JsonElement, bool> done)
    {
        var frames = new List<JsonElement>();
        do { frames.Add(await Read(socket)); } while (!done(frames.Last()));
        return frames;
    }
    private static void Publish(LiveHub hub, string session, string text) =>
        hub.Publish(session, new RowConverter().Apply(new TurnStarted(1000, Guid.NewGuid().ToString("N"), text)));
    private static void Stats(LiveHub hub, string session, long total)
    {
        var converter = new RowConverter();
        converter.Apply(new TurnStarted(1000, Guid.NewGuid().ToString("N"), "turn"));
        hub.Publish(session, converter.Apply(new TurnEnded(1001, "end_turn", new TokenUsage(1, 1, null, null, total))));
    }
    private sealed class Removed(DirectoryInfo folder) : IDisposable
    {
        public void Dispose() => folder.Delete(recursive: true);
    }
    private static void Tool(LiveHub hub, string session, string item, string payload)
    {
        var batch = new RowBatch();
        batch.Rows.Add(new("run-1", item, RowKind.Tool, JsonDocument.Parse(payload).RootElement.Clone(), 1000));
        hub.Publish(session, batch);
    }
    private static void Child(LiveHub hub, string session, string child)
    {
        var batch = new RowBatch();
        batch.Rows.Add(new(null, "subagent:" + child, RowKind.Subagent, JsonSerializer.SerializeToElement(
            new SubagentPayload(child, child, null, "idle", null, null, false, null), RowJsonContext.Rows.SubagentPayload), 1000));
        hub.Publish(session, batch);
    }

    private sealed class TrackingSources(IRowSourceBindings inner) : IRowSourceBindings
    {
        internal readonly List<ExpectedSource> Captures = [];
        internal readonly Channel<JsonElement> Enqueued = Channel.CreateUnbounded<JsonElement>();
        internal readonly Channel<bool> Disposed = Channel.CreateUnbounded<bool>();
        internal Func<CancellationToken, Task>? BeforeCapture;
        internal int Attempts;
        public async Task<RowSourceBinding> CaptureAsync(ExpectedSource source, string session, CancellationToken ct)
        {
            Interlocked.Increment(ref Attempts);
            if (BeforeCapture is { } before) await before(ct);
            var actual = await inner.CaptureAsync(source, session, ct);
            Captures.Add(source);
            return new Tracked(actual, this);
        }
        private sealed class Tracked(RowSourceBinding inner, TrackingSources owner) : RowSourceBinding
        {
            private int _disposed;
            public override bool Remote => inner.Remote;
            public override CancellationToken Changed => inner.Changed;
            public override void EnsureCurrent() => inner.EnsureCurrent();
            public override void Watch() => inner.Watch();
            public override string? ActiveRun => inner.ActiveRun;
            public override Task<JsonElement> PageAsync(long before, int limit, CancellationToken ct) => inner.PageAsync(before, limit, ct);
            public override Task<SubagentPage> SubagentsAsync(string? after, int limit, CancellationToken ct) => inner.SubagentsAsync(after, limit, ct);
            public override Task<IReadOnlyList<FileTouched>> FilesAsync(CancellationToken ct) => inner.FilesAsync(ct);
            public override Task<SessionTokens> TokensAsync(CancellationToken ct) => inner.TokensAsync(ct);
            public override async IAsyncEnumerable<JsonElement> WatchAsync(long? after, [EnumeratorCancellation] CancellationToken ct)
            {
                await foreach (var frame in inner.WatchAsync(after, ct))
                {
                    yield return frame;
                    // The actual consumer has queued this frame and requested the next, rather than a timing guess.
                    owner.Enqueued.Writer.TryWrite(frame);
                }
            }
            public override void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                inner.Dispose(); owner.Disposed.Writer.TryWrite(true);
            }
        }
    }

    private sealed class RemoteFixture : IAsyncDisposable
    {
        internal readonly TwoHosts Hosts = new(forwardCommands: true);
        internal SocketHost Socket = null!;
        internal TrackingSources Sources = null!, OwnerSources = null!;
        internal LiveHub OwnerHub = null!;
        internal string Session = null!;
        internal ExpectedSource Expected = null!;
        private readonly List<LiveHub> _ownerHubs = [];
        private LiveHub _localHub = null!;
        internal LiveHub LocalHub => _localHub;
        internal static async Task<RemoteFixture> Start()
        {
            var f = new RemoteFixture();
            try
            {
                f.Hosts.Remote.AdditionalFeatures = calls =>
                {
                    var host = f.Hosts.Remote;
                    var store = RowStore.Open(host.Fixture.Db);
                    var machines = new MachineSet(new FakeMachine(null, host.Sessions));
                    f.OwnerHub = new(store, topics: host.Topics); f._ownerHubs.Add(f.OwnerHub);
                    f.OwnerSources = new(new SessionRowSources(host.Fixture.Db, host.Sessions, machines));
                    new RowsFeature(f.OwnerHub, store, host.Sessions, machines, new DiskPathProbe(), f.OwnerSources).Register(calls);
                };
                await f.Hosts.Start();
                f.Session = f.Hosts.Remote.Sessions.Create("codex", "repo").SessionId;
                f.Hosts.Remote.Fixture.SeedSession(f.Session);
                Publish(f.OwnerHub, f.Session, "OWNER_ONLY"); Child(f.OwnerHub, f.Session, "owner-child");
                await f.Hosts.StartMirror();
                await f.Ready();
                var local = f.Hosts.Mac;
                var store = RowStore.Open(local.Fixture.Db);
                f._localHub = new(store, topics: local.Topics);
                Publish(f._localHub, f.Session, "POISONED_COPY"); Child(f._localHub, f.Session, "poison-child");
                var machines = new MachineSet(new FakeMachine(null, local.Sessions));
                f.Sources = new(new SessionRowSources(local.Fixture.Db, local.Sessions, machines, f.Hosts.Connections));
                f.Socket = await SocketHost.Start(local.Fixture, local.Topics, new RowsFeature(f._localHub, store, local.Sessions, machines, new DiskPathProbe(), f.Sources));
                f.Expected = f.CurrentSource();
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        private async Task Ready()
        {
            var mirror = Hosts.Connections.CaptureMirrorOwners().Single().Mirror;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void CaughtUp() => ready.TrySetResult();
            mirror.CaughtUp += CaughtUp;
            try
            {
                if (await mirror.CaptureLiveBindingAsync(default) is not null) ready.TrySetResult();
                await ready.Task.WaitAsync(Wait);
            }
            finally { mirror.CaughtUp -= CaughtUp; }
        }
        internal ExpectedSource CurrentSource() => new(Hosts.Remote.Fixture.Machine,
            Hosts.Mac.Fixture.Number("SELECT generation FROM engine_mirror WHERE machine=$machine AND state='live'",
                new SqlArg("$machine", Hosts.Remote.Fixture.Machine)), "workbox-test");
        public async ValueTask DisposeAsync()
        {
            if (Socket is not null) await Socket.DisposeAsync();
            await Hosts.DisposeAsync();
            _localHub?.Dispose(); foreach (var hub in _ownerHubs) hub.Dispose();
        }
    }

    private sealed class SocketHost : IAsyncDisposable
    {
        private WebApplication _app = null!;
        private Uri _address = null!;
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        internal static async Task<SocketHost> Start(EngineFixture f, TopicSet topics, RowsFeature rows)
        {
            var host = new SocketHost();
            var calls = new SocketDispatcher(f.Db.Writer, topics, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            rows.Register(calls);
            calls.Read("probe", "echo", RowJsonContext.Rows.JsonElement, RowJsonContext.Rows.JsonElement, (args, _) => Task.FromResult(args));
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            host._app = builder.Build(); host._app.UseWebSockets();
            void Map(string path, bool bulk) => host._app.Map(path, async context =>
            {
                using var raw = await context.WebSockets.AcceptWebSocketAsync();
                using var socket = new GatedSocket(raw, host);
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, host._stop.Token);
                await new SocketConnection(socket, calls, TimeProvider.System, bulk).RunAsync(Encoding.UTF8.GetBytes("rows-token"), f.Machine, stop.Token, f.Machine);
            });
            Map("/socket", false); Map("/socket/bulk", true);
            host._app.Lifetime.ApplicationStopping.Register(host._stop.Cancel);
            await host._app.StartAsync();
            host._address = new Uri(host._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            return host;
        }
        internal async Task<ClientWebSocket> Open(bool bulk = false)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new UriBuilder(_address) { Scheme = "ws", Path = bulk ? "/socket/bulk" : "/socket" }.Uri, CancellationToken.None);
            await Send(socket, new { t = "hello", protocol = 1, token = "rows-token" });
            Assert.Equal("welcome", Type(await Read(socket))); return socket;
        }
        internal async Task Block(WebSocket client)
        {
            Interlocked.Exchange(ref _armed, 1);
            await Send(client, new { t = "call", id = 900, feature = "probe", method = "echo", args = new { } });
            await _blocked.Task.WaitAsync(Wait);
        }
        internal void Release() => _release.TrySetResult();
        public async ValueTask DisposeAsync()
        {
            Release(); await _stop.CancelAsync(); await _app.DisposeAsync(); _stop.Dispose();
        }
        private sealed class GatedSocket(WebSocket inner, SocketHost host) : WebSocket
        {
            public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
            public override string? CloseStatusDescription => inner.CloseStatusDescription;
            public override WebSocketState State => inner.State;
            public override string? SubProtocol => inner.SubProtocol;
            public override void Abort() => inner.Abort();
            public override void Dispose() => inner.Dispose();
            public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => inner.CloseAsync(status, description, ct);
            public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => inner.CloseOutputAsync(status, description, ct);
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) => inner.ReceiveAsync(buffer, ct);
            public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
            {
                await Gate(buffer.AsMemory(), type); await inner.SendAsync(buffer, type, end, ct);
            }
            public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct = default)
            {
                await Gate(buffer, type); await inner.SendAsync(buffer, type, end, ct);
            }
            private async Task Gate(ReadOnlyMemory<byte> buffer, WebSocketMessageType type)
            {
                if (type != WebSocketMessageType.Text || Volatile.Read(ref host._armed) == 0) return;
                using var frame = JsonDocument.Parse(buffer);
                if (!frame.RootElement.TryGetProperty("id", out var id) || id.GetInt64() != 900) return;
                if (Interlocked.Exchange(ref host._armed, 0) == 0) return;
                host._blocked.TrySetResult(); await host._release.Task.WaitAsync(host._stop.Token);
            }
        }
    }
}
