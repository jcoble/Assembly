using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Chat;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Infrastructure.Machines;
using Assembly.Tests.Draft.Base.Chat;

namespace Assembly.Tests.Infrastructure.Remote.Attachments;

public sealed class AttachmentSourceTests
{
    [Fact]
    public async Task Captured_remote_upload_survives_begin_ACK_and_commit_cleanup_and_reads_exact_bytes()
    {
        await using var f = await Fixture.Start();
        using var socket = await Open(f.Hosts.Mac.Address, true);
        var bytes = Image(AttachmentsFeature.ChunkBytes + 137);
        var begun = Result(await Call(socket, 1, "attachments", "begin", new AttachmentsFeature.BeginArgs(Fixture.Session, "image/png", bytes.Length, f.Source), AttachmentsJson.Default.BeginArgs));
        var upload = begun.GetProperty("upload").GetString()!;
        Error(await Call(socket, 999, "attachments", "chunk", new AttachmentsFeature.ChunkArgs(upload, 0, []), AttachmentsJson.Default.ChunkArgs, f.Source), "bad_args");
        for (var offset = 0; offset < bytes.Length; offset += AttachmentsFeature.ChunkBytes)
        {
            var chunk = bytes.Skip(offset).Take(AttachmentsFeature.ChunkBytes).ToArray();
            var received = Result(await Call(socket, offset + 2, "attachments", "chunk", new AttachmentsFeature.ChunkArgs(upload, offset, chunk), AttachmentsJson.Default.ChunkArgs));
            Assert.Equal(offset + chunk.Length, received.GetProperty("bytes").GetInt64());
        }
        var saved = Result(await Call(socket, 9, "attachments", "commit", new AttachmentsFeature.CommitArgs(upload), AttachmentsJson.Default.CommitArgs));
        var path = saved.GetProperty("path").GetString()!;
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.StartsWith(f.RemoteImages, path);
        Assert.Empty(f.Staged());
        Assert.Empty(f.Staged(remote: true));
        var read = new List<byte>();
        for (var offset = 0; offset < bytes.Length; offset += AttachmentsFeature.ChunkBytes)
        {
            var reply = Result(await Call(socket, offset + 20, "attachments", "read", new AttachmentsFeature.ReadArgs(Fixture.Session, Path.GetFileName(path), offset), AttachmentsJson.Default.ReadArgs, f.Source));
            Assert.Equal(bytes.Length, reply.GetProperty("size").GetInt64());
            read.AddRange(reply.GetProperty("data").GetBytesFromBase64());
        }
        Assert.Equal(bytes, read);
    }

    [Fact]
    public async Task Owner_collision_and_contradictory_source_refuse_before_staging_and_legacy_upload_still_works()
    {
        await using var f = await Fixture.Start(); using var socket = await Open(f.Hosts.Mac.Address, true);
        var begin = new AttachmentsFeature.BeginArgs(Fixture.Session, "image/png", 16, f.Source);
        Error(await Call(socket, 1, "attachments", "begin", begin, AttachmentsJson.Default.BeginArgs, f.Source with { Generation = f.Source.Generation + 1 }), "bad_args");
        Error(await Call(socket, 2, "attachments", "begin", begin with { ExpectedSource = new(f.Hosts.Mac.Fixture.Machine, 0, null) }, AttachmentsJson.Default.BeginArgs), "source_changed");
        f.Gate.OverridePeer = true; f.Gate.Peer = new PhonePeer("actual-peer-device");
        Error(await Call(socket, 3, "attachments", "begin", begin, AttachmentsJson.Default.BeginArgs), "forbidden");
        f.Gate.OverridePeer = false;
        Assert.Empty(f.Staged()); Assert.Empty(f.Saved());
        var framed = Result(await Call(socket, 7, "attachments", "begin", begin with { ExpectedSource = null }, AttachmentsJson.Default.BeginArgs, f.Source)).GetProperty("upload").GetString()!;
        Result(await Call(socket, 8, "attachments", "drop", new AttachmentsFeature.DropArgs(framed), AttachmentsJson.Default.DropArgs));
        Assert.Empty(f.Staged());
        f.Hosts.Mac.Sessions.Add("legacy-local", "codex", "fixture-repo", null!);
        var bytes = Image(16);
        var upload = Result(await Call(socket, 4, "attachments", "begin", new AttachmentsFeature.BeginArgs("legacy-local", "image/png", bytes.Length), AttachmentsJson.Default.BeginArgs)).GetProperty("upload").GetString()!;
        Result(await Call(socket, 5, "attachments", "chunk", new AttachmentsFeature.ChunkArgs(upload, 0, bytes), AttachmentsJson.Default.ChunkArgs));
        var path = Result(await Call(socket, 6, "attachments", "commit", new AttachmentsFeature.CommitArgs(upload), AttachmentsJson.Default.CommitArgs)).GetProperty("path").GetString()!;
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.StartsWith(f.MacImages, path); Assert.Empty(f.Staged());
    }

    [Fact]
    public async Task Explicit_source_without_actual_bindings_refuses_even_an_unknown_session_before_IO()
    {
        await using var f = await Fixture.Start(bindings: false); using var socket = await Open(f.Hosts.Mac.Address, true);
        Error(await Call(socket, 1, "attachments", "begin", new AttachmentsFeature.BeginArgs("absent", "image/png", 16, f.Source), AttachmentsJson.Default.BeginArgs), "source_changed");
        Assert.Empty(f.Staged()); Assert.Empty(f.Saved());
    }

    [Fact]
    public async Task Source_replacement_after_chunk_IO_aborts_transfer_without_retargeting_or_saving()
    {
        await using var f = await Fixture.Start(); using var socket = await Open(f.Hosts.Mac.Address, true);
        var bytes = Image(16); var upload = await Begin(f, socket, bytes.Length);
        var entered = Signal(); var release = Signal();
        f.Gate.Prepared = async (method, call) => { if (method == "chunk") { entered.TrySetResult(); await release.Task.WaitAsync(call.Cancellation); } };
        var chunk = Call(socket, 2, "attachments", "chunk", new AttachmentsFeature.ChunkArgs(upload, 0, bytes), AttachmentsJson.Default.ChunkArgs);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var replacement = new FakeMachine("workbox-test", f.Hosts.Mac.Sessions); f.Machines.Set(replacement);
        await f.Hosts.DisconnectMirror(); release.TrySetResult();
        Error(await chunk, "source_changed");
        Assert.Null(replacement.Saved); Assert.Empty(f.Staged()); Assert.Empty(f.Saved());
        Error(await Call(socket, 3, "attachments", "commit", new AttachmentsFeature.CommitArgs(upload), AttachmentsJson.Default.CommitArgs), "failed");
    }

    [Fact]
    public async Task Committed_machine_removal_reclaims_an_idle_upload_without_another_upload_call()
    {
        await using var f = await Fixture.Start(); using var socket = await Open(f.Hosts.Mac.Address, true);
        var upload = await Begin(f, socket, 16);
        Assert.Single(f.Staged());
        var profiles = new MachineStore(f.Hosts.Mac.Fixture.Db);
        f.Hosts.Mac.Fixture.Db.Writer.Write("test.remove-machine", scope => { profiles.Remove(scope, "workbox-test"); return 0; });
        // Join the existing owner lifecycle queued by the committed saved-machines tombstone.
        await f.Hosts.Connections.ReadyAsync("workbox-test").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(profiles.Get("workbox-test", default));
        Assert.Empty(f.Staged()); Assert.Empty(f.Saved());
        Error(await Call(socket, 2, "attachments", "commit", new AttachmentsFeature.CommitArgs(upload), AttachmentsJson.Default.CommitArgs), "failed");
    }

    [Fact]
    public async Task Post_save_source_change_refuses_stale_ACK_as_uncertain_without_claiming_file_rollback()
    {
        await using var f = await Fixture.Start(); using var socket = await Open(f.Hosts.Mac.Address, true);
        var bytes = Image(16); var upload = await Begin(f, socket, bytes.Length);
        Result(await Call(socket, 2, "attachments", "chunk", new AttachmentsFeature.ChunkArgs(upload, 0, bytes), AttachmentsJson.Default.ChunkArgs));
        var entered = Signal(); var release = Signal();
        f.Gate.Prepared = async (method, call) => { if (method == "commit") { entered.TrySetResult(); await release.Task.WaitAsync(call.Cancellation); } };
        var commit = Call(socket, 3, "attachments", "commit", new AttachmentsFeature.CommitArgs(upload), AttachmentsJson.Default.CommitArgs);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(f.Saved());
        await f.Hosts.DisconnectMirror(); release.TrySetResult();
        Error(await commit, "uncertain");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(f.Saved())));
        Assert.Empty(f.Staged());
    }

    [Fact]
    public async Task Changed_source_after_successful_writer_refuses_the_queued_ACK_and_disposes_its_guard()
    {
        await using var f = await Fixture.Start(); using var socket = await Open(f.Hosts.Mac.Address, true);
        var bytes = Image(16); var upload = await Begin(f, socket, bytes.Length);
        Result(await Call(socket, 2, "attachments", "chunk", new AttachmentsFeature.ChunkArgs(upload, 0, bytes), AttachmentsJson.Default.ChunkArgs));
        var committed = Signal(); var sender = Signal(); var release = Signal(); var disposed = Signal();
        f.Hosts.Mac.WriteCommitted = what => { if (what == "attachments/commit") committed.TrySetResult(); };
        f.Gate.ReplyGuard = (method, guard) => method == "commit" ? new SenderGuard(guard, sender, release, disposed) : guard;
        var commit = Call(socket, 3, "attachments", "commit", new AttachmentsFeature.CommitArgs(upload), AttachmentsJson.Default.CommitArgs);
        try
        {
            await sender.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Single(f.Saved()); Assert.Empty(f.Staged());
            await f.Hosts.DisconnectMirror(); release.TrySetResult();
            Error(await commit, "source_changed");
            await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(f.Saved())));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Cancelled_chunk_and_closed_socket_reclaim_owned_staging_without_saving()
    {
        await using var f = await Fixture.Start(); using var socket = await Open(f.Hosts.Mac.Address, true);
        var bytes = Image(16); var upload = await Begin(f, socket, bytes.Length);
        var entered = Signal();
        f.Gate.Prepared = async (method, call) => { if (method == "chunk") { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, call.Cancellation); } };
        var chunk = Call(socket, 2, "attachments", "chunk", new AttachmentsFeature.ChunkArgs(upload, 0, bytes), AttachmentsJson.Default.ChunkArgs);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Send(socket, "{\"t\":\"cancel\",\"id\":2}");
        Error(await chunk, "cancelled"); Assert.Empty(f.Staged()); Assert.Empty(f.Saved());
        f.Gate.Prepared = null;
        await Begin(f, socket, bytes.Length);
        Assert.Single(f.Staged()); socket.Abort();
        await f.Gate.ClosedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(f.Staged()); Assert.Empty(f.Saved());
    }

    [Fact]
    public async Task Remote_context_reads_use_owner_rows_not_same_ID_cached_rows_and_return_actual_route()
    {
        await using var f = await Fixture.Start();
        var ownerRows = RowStore.Open(f.Hosts.Remote.Fixture.Db); var cachedRows = RowStore.Open(f.Hosts.Mac.Fixture.Db);
        var owner = new RowBatch(); owner.Rows.Add(new("turn", "user", RowKind.User, JsonSerializer.SerializeToElement(new { text = "owner prompt\nAttached image: /owner.png" }), 1));
        var cache = new RowBatch(); cache.Rows.Add(new("turn", "user", RowKind.User, JsonSerializer.SerializeToElement(new { text = "wrong cached prompt\nAttached image: /cache.png" }), 1));
        owner.Rows.Add(new("turn", "tool", RowKind.Tool, JsonSerializer.SerializeToElement(new { kind = "edit", state = "completed", path = "owner.txt", added = 2, removed = 1 }), 2));
        cache.Rows.Add(new("turn", "tool", RowKind.Tool, JsonSerializer.SerializeToElement(new { kind = "edit", state = "completed", path = "wrong-cache.txt", added = 99, removed = 0 }), 2));
        ownerRows.Commit(Fixture.Session, owner); cachedRows.Commit(Fixture.Session, cache);
        using var socket = await Open(f.Hosts.Mac.Address);
        var args = new RowsFeature.SessionArgs(Fixture.Session, f.Source);
        var reply = Result(await Call(socket, 1, "rows", "attachments", args with { ExpectedSource = null }, RowJsonContext.Rows.SessionArgs, f.Source));
        Assert.Equal("workbox-test", reply.GetProperty("machine").GetString());
        var prompt = Assert.Single(reply.GetProperty("prompts").EnumerateArray()).GetString();
        Assert.Contains("owner prompt", prompt); Assert.DoesNotContain("wrong cached", prompt);
        var file = Assert.Single(Result(await Call(socket, 2, "rows", "files", args, RowJsonContext.Rows.SessionArgs)).EnumerateArray());
        Assert.Equal("owner.txt", file.GetProperty("path").GetString()); Assert.Equal(2, file.GetProperty("added").GetInt64());
        Error(await Call(socket, 3, "rows", "attachments", args, RowJsonContext.Rows.SessionArgs, f.Source with { Generation = f.Source.Generation + 1 }), "bad_args");
        var legacy = Result(await Call(socket, 4, "rows", "attachments", new RowsFeature.SessionArgs(Fixture.Session), RowJsonContext.Rows.SessionArgs));
        Assert.Contains("wrong cached", Assert.Single(legacy.GetProperty("prompts").EnumerateArray()).GetString());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Session = "attachment-session";
        public TwoHosts Hosts { get; } = new(forwardCommands: true);
        private readonly string _home = Directory.CreateTempSubdirectory("assembly-source-upload-").FullName;
        public string MacImages => Path.Combine(_home, "mac-images");
        public string RemoteImages => Path.Combine(_home, "remote-images");
        public string Staging => Path.Combine(_home, "mac-staging");
        public MachineSet Machines = null!;
        public Gate Gate = null!;
        public ExpectedSource Source = null!;
        public string[] Staged(bool remote = false) => Directory.Exists(remote ? Path.Combine(_home, "remote-staging") : Staging)
            ? Directory.GetFiles(remote ? Path.Combine(_home, "remote-staging") : Staging) : [];
        public string[] Saved() => Directory.Exists(RemoteImages) ? Directory.GetFiles(RemoteImages, "*", SearchOption.AllDirectories) : [];
        public static async Task<Fixture> Start(bool bindings = true)
        {
            var f = new Fixture(); ICallRegistry macCalls = null!;
            f.Hosts.Remote.AdditionalFeatures = calls =>
            {
                var local = new LocalMachine(f.Hosts.Remote.NeedHub, f.Hosts.Remote.Sessions, f.Hosts.Remote.NeedHosts, new AttachmentStore(f.RemoteImages));
                var machines = new MachineSet(local);
                var sources = new SessionRowSources(f.Hosts.Remote.Fixture.Db, f.Hosts.Remote.Sessions, machines);
                new AttachmentsFeature(f.Hosts.Remote.Sessions, machines, Path.Combine(f._home, "remote-staging"), sources).Register(calls);
                new RowsFeature(f.Hosts.Remote.NeedHub, RowStore.Open(f.Hosts.Remote.Fixture.Db), f.Hosts.Remote.Sessions, machines, new DiskPathProbe(), sources).Register(calls);
            };
            f.Hosts.Mac.AdditionalFeatures = calls => macCalls = calls;
            await f.Hosts.Start();
            f.Hosts.Remote.Sessions.Add(Session, "codex", "fixture-repo", null!); f.Hosts.Remote.Fixture.SeedSession(Session);
            f.Hosts.Mac.Sessions.Add(Session, "codex", "fixture-repo", "workbox-test");
            await f.Hosts.StartMirror();
            var localMac = new LocalMachine(f.Hosts.Mac.NeedHub, f.Hosts.Mac.Sessions, f.Hosts.Mac.NeedHosts, new AttachmentStore(f.MacImages));
            f.Machines = new(localMac, f.Hosts.ConnectedMachine);
            var source = new SessionRowSources(f.Hosts.Mac.Fixture.Db, f.Hosts.Mac.Sessions, f.Machines, f.Hosts.Connections);
            f.Gate = new(macCalls);
            new AttachmentsFeature(f.Hosts.Mac.Sessions, f.Machines, f.Staging, bindings ? source : null).Register(f.Gate);
            new RowsFeature(f.Hosts.Mac.NeedHub, RowStore.Open(f.Hosts.Mac.Fixture.Db), f.Hosts.Mac.Sessions, f.Machines, new DiskPathProbe(), bindings ? source : null).Register(f.Gate);
            f.Source = new(f.Hosts.Remote.Fixture.Machine, f.Hosts.Mac.Fixture.Number("SELECT generation FROM engine_mirror WHERE machine=$machine", new SqlArg("$machine", f.Hosts.Remote.Fixture.Machine)), "workbox-test");
            return f;
        }
        public async ValueTask DisposeAsync() { await Hosts.DisposeAsync(); Directory.Delete(_home, true); }
    }

    private sealed class Gate(ICallRegistry inner) : ICallRegistry
    {
        public bool OverridePeer; public CallPeer? Peer;
        public Func<string, CallContext, Task>? Prepared;
        public Func<string, IExpectedSourceGuard, IExpectedSourceGuard>? ReplyGuard;
        public TaskCompletionSource ClosedSignal { get; } = Signal();
        public CallKind? KindOf(string feature, string method) => inner.KindOf(feature, method);
        public void Read<A, R>(string feature, string method, JsonTypeInfo<A> args, JsonTypeInfo<R> result, Func<A, CallContext, Task<R>> handler, bool bulk = false) =>
            inner.Read(feature, method, args, result, async (value, call) =>
            {
                var context = OverridePeer ? call with { Peer = Peer } : call;
                try { return await handler(value, context); }
                finally { call.SourceGuard = context.SourceGuard; }
            }, bulk);
        public void Write<A, R>(string feature, string method, JsonTypeInfo<A> args, JsonTypeInfo<R> result, Func<A, CallContext, Task<Func<IWriteScope, R>>> handler,
            Func<A, string?>? order = null, Action<A, CallContext>? failed = null, bool bulk = false) =>
            inner.Write(feature, method, args, result, async (value, call) =>
            {
                var context = OverridePeer ? call with { Peer = Peer } : call;
                Func<IWriteScope, R> apply;
                try { apply = await handler(value, context); }
                finally { call.SourceGuard = context.SourceGuard; }
                if (Prepared is { } prepared) await prepared(method, call);
                if (call.SourceGuard is { } guard && ReplyGuard is { } wrap) call.SourceGuard = wrap(method, guard);
                return apply;
            }, order, failed, bulk);
        public void Save<T>(string feature, JsonTypeInfo<T> type, Action<string, T, IWriteScope> save) => inner.Save(feature, type, save);
        public void Closed(Action<string> closed) => inner.Closed(connection => { closed(connection); ClosedSignal.TrySetResult(); });
        public void Topic<T>(string topic, JsonTypeInfo<T> type, Func<T, string> key, Func<string?, CallContext, IReadOnlyList<T>> state, bool terminal = false) => inner.Topic(topic, type, key, state, terminal);
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state, Action<string, CallContext>? opened = null) => inner.Rows(topic, state, opened);
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state, Action<string, CallContext>? opened, IRowSourceBindings sources) => inner.Rows(topic, state, opened, sources);
    }

    private sealed class SenderGuard(IExpectedSourceGuard inner, TaskCompletionSource entered, TaskCompletionSource release,
        TaskCompletionSource disposed) : IExpectedSourceGuard
    {
        private int _checks, _disposed;
        public CancellationToken Changed => inner.Changed;
        public void EnsureCurrent()
        {
            // First: dispatcher after writer. Second: actual SourceBoundReply dequeued by the sender.
            if (Interlocked.Increment(ref _checks) == 2)
            {
                entered.TrySetResult();
                release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            inner.EnsureCurrent();
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            inner.Dispose(); disposed.TrySetResult();
        }
    }

    private static byte[] Image(int count) { var bytes = new byte[count]; new Random(17).NextBytes(bytes); new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0); return bytes; }
    private static async Task<string> Begin(Fixture f, WebSocket socket, int size) => Result(await Call(socket, 1, "attachments", "begin",
        new AttachmentsFeature.BeginArgs(Fixture.Session, "image/png", size, f.Source), AttachmentsJson.Default.BeginArgs)).GetProperty("upload").GetString()!;
    private static JsonElement Result(JsonElement frame) { Assert.Equal("result", frame.GetProperty("t").GetString()); return frame.GetProperty("result"); }
    private static void Error(JsonElement frame, string reason) { Assert.Equal("error", frame.GetProperty("t").GetString()); Assert.Equal(reason, frame.GetProperty("code").GetString()); Assert.False(frame.TryGetProperty("result", out _)); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<ClientWebSocket> Open(Uri address, bool bulk = false)
    {
        var socket = new ClientWebSocket(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri($"ws://{address.Authority}/socket{(bulk ? "/bulk" : "")}"), timeout.Token);
        await Send(socket, "{\"t\":\"hello\",\"token\":\"isolated-token\",\"protocol\":1}");
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString()); return socket;
    }
    private static async Task<JsonElement> Call<T>(WebSocket socket, long id, string feature, string method, T args, JsonTypeInfo<T> type, ExpectedSource? frameSource = null)
    {
        await Send(socket, $$"""{"t":"call","id":{{id}},"feature":"{{feature}}","method":"{{method}}","args":{{JsonSerializer.Serialize(args, type)}}{{(frameSource is null ? "" : ",\"expectedSource\":" + JsonSerializer.Serialize(frameSource, RowJsonContext.Rows.ExpectedSource))}}}""");
        return await Read(socket);
    }
    private static Task Send(WebSocket socket, string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Read(WebSocket socket)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[65536]; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); WebSocketReceiveResult part;
        do { part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token); Assert.Equal(WebSocketMessageType.Text, part.MessageType); bytes.Write(buffer, 0, part.Count); } while (!part.EndOfMessage);
        using var json = JsonDocument.Parse(bytes.ToArray()); return json.RootElement.Clone();
    }
}
