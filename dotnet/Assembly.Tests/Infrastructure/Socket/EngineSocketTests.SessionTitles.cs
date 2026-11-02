using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using System.Text.Json.Serialization.Metadata;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Socket;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class EngineSocketTests
{
    [Fact]
    public async Task Registered_title_stat_and_bulk_chunk_preserve_exact_one_mib_and_refuse_mutation()
    {
        _f.SeedSession();
        var title = "\0" + new string('x', 1048574) + "😀\0suffix";
        var context = new CommandContext(Guid.NewGuid(), _f.Machine, new(ActorKind.System, "test"), "s", 0, 0, 1000);
        Assert.True(_f.Db.Writer.Write("title", s => _runtime.Commands.ApplyPreparedSessionTitle(s, context, EngineCommands.PrepareSessionTitle(new ReportSessionTitle(title)))).Applied);
        using var normal = await Open();
        await Send(normal, $"{{\"t\":\"call\",\"id\":{long.MaxValue},\"feature\":\"engine\",\"method\":\"session_title\",\"args\":{{\"session\":\"s\"}}}}");
        using var statFrame = JsonDocument.Parse(await OwnerWire(normal));
        var stat = statFrame.RootElement.GetProperty("result").Deserialize(EngineSocketJson.Default.SessionTitleStat)!;
        Assert.Equal(_f.Machine, stat.EngineMachine); Assert.Equal(Encoding.UTF8.GetByteCount(title), stat.Utf8Bytes);
        using var bulk = await Open(bulk: true);
        var args = new SessionTitleChunkArgs("s", stat.Version, stat.Epoch, stat.Utf8Bytes, 0, Machine: stat.EngineMachine);
        async Task<byte[]> Read(SessionTitleChunkArgs request)
        {
            await Send(bulk, $"{{\"t\":\"call\",\"id\":{long.MaxValue},\"feature\":\"engine\",\"method\":\"session_title_chunk\",\"args\":{JsonSerializer.Serialize(request, EngineSocketJson.Default.SessionTitleChunkArgs)}}}");
            return await OwnerWire(bulk);
        }
        var wire = await Read(args); using var frame = JsonDocument.Parse(wire);
        var chunk = frame.RootElement.GetProperty("result").Deserialize(EngineSocketJson.Default.SessionTitleChunk)!;
        Assert.Equal(1048576, chunk.Data.Length); Assert.Equal(1048576, chunk.NextOffset);
        Assert.Equal(Encoding.UTF8.GetBytes(title)[..1048576], chunk.Data);
        Assert.InRange(wire.Length, 1398104, 4 * 1024 * 1024);
        Assert.Equal(wire.Length, SocketDispatcher.Result(long.MaxValue, frame.RootElement.GetProperty("result")).Length);
        Assert.True(_f.Apply(new ReportCapabilities(true), "s", stat.Version, actor: new(ActorKind.System, "test")).Applied);
        using var changed = JsonDocument.Parse(await Read(args));
        Assert.Equal("error", changed.RootElement.GetProperty("t").GetString());
        Assert.Contains("title_changed", changed.RootElement.GetRawText());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Registered_legacy_title_reads_preserve_empty_NUL_and_full_UTF8_across_metadata_progress(int kind)
    {
        var title = kind switch { 0 => "", 1 => "before\0after😀", _ => new string('界', 400000) + "\0FINAL😀" };
        var reference = LegacyTitle(title);
        using var normal = await Open(); using var bulk = await Open(bulk: true);
        var stat = (await TitleCall(normal, "legacy_session_title", new LegacySessionTitleStatArgs(reference),
            EngineSocketJson.Default.LegacySessionTitleStatArgs)).GetProperty("result").Deserialize(EngineSocketJson.Default.LegacySessionTitleStat)!;
        Assert.Equal(reference, stat.Reference); Assert.Null(stat.OwningEngineVersion); Assert.Null(stat.OwningEngineEpoch);
        var expected = Encoding.UTF8.GetBytes(title); using var body = new MemoryStream(); long offset = 0;
        do
        {
            _f.Time.Advance(TimeSpan.FromSeconds(1)); _titleSessions.Touch(reference.SessionId);
            var frame = await TitleCall(bulk, "legacy_session_title_chunk", new LegacySessionTitleChunkArgs(reference, offset, 1024 * 1024),
                EngineSocketJson.Default.LegacySessionTitleChunkArgs);
            var chunk = frame.GetProperty("result").Deserialize(EngineSocketJson.Default.LegacySessionTitleChunk)!;
            Assert.Equal(offset, chunk.Offset); Assert.Equal(expected.LongLength, chunk.TotalBytes);
            Assert.InRange(SocketDispatcher.Result(long.MaxValue, frame.GetProperty("result")).Length, 1, 4 * 1024 * 1024);
            body.Write(chunk.Data); offset += chunk.Data.LongLength;
            Assert.Equal(offset < expected.LongLength ? offset : (long?)null, chunk.NextOffset);
        } while (offset < expected.LongLength);
        Assert.Equal(expected, body.ToArray());
        var current = (await TitleCall(normal, "legacy_session_title", new LegacySessionTitleStatArgs(reference),
            EngineSocketJson.Default.LegacySessionTitleStatArgs)).GetProperty("result").Deserialize(EngineSocketJson.Default.LegacySessionTitleStat)!;
        Assert.True(current.Reference.UpdatedAt > reference.UpdatedAt);
        Assert.Equal(reference with { UpdatedAt = current.Reference.UpdatedAt }, current.Reference);
    }

    [Theory]
    [InlineData("equal", true)]
    [InlineData("different-full-BLOB", false)]
    [InlineData("foreign", false)]
    [InlineData("remote-holder", false)]
    [InlineData("other-machine", false)]
    public async Task Legacy_stat_reports_genuine_Engine_facts_only_for_same_scope_local_owner_and_exact_BLOB(string state, bool owning)
    {
        var reference = LegacyTitle("same-prefix\0LEGACY");
        _f.SeedSession("legacy");
        _f.Db.Writer.Write("test.owning_title_facts", scope => scope.Execute("""
            UPDATE engine_session SET title=CAST($title AS TEXT),version=7,epoch=4,state=$state,holder=$holder,machine=$machine WHERE id='legacy'
            """, new SqlArg("$title", Encoding.UTF8.GetBytes(state == "different-full-BLOB" ? "same-prefix\0ENGINE" : "same-prefix\0LEGACY")),
            new SqlArg("$state", state == "foreign" ? "foreign" : "idle"),
            new SqlArg("$holder", state == "remote-holder" ? "remote" : "local"),
            new SqlArg("$machine", state is "foreign" or "other-machine" ? "different-engine" : _runtime.Machine)));
        using var normal = await Open();
        var stat = (await TitleCall(normal, "legacy_session_title", new LegacySessionTitleStatArgs(reference),
            EngineSocketJson.Default.LegacySessionTitleStatArgs)).GetProperty("result").Deserialize(EngineSocketJson.Default.LegacySessionTitleStat)!;
        Assert.Equal(owning ? 7 : (long?)null, stat.OwningEngineVersion);
        Assert.Equal(owning ? 4 : (long?)null, stat.OwningEngineEpoch);
        Assert.Equal(reference, stat.Reference);
    }

    [Fact]
    public async Task Registered_legacy_title_reads_fail_closed_on_publisher_storage_and_content_changes()
    {
        var reference = LegacyTitle("Original\0full");
        using var normal = await Open(); using var bulk = await Open(bulk: true);
        foreach (var forged in new[] { reference with { PublisherEngineMachine = "forged" }, reference with { StoredMachine = null },
            reference with { ParentSessionId = "other-parent" }, reference with { TitleSource = "helper" },
            reference with { Utf8Bytes = reference.Utf8Bytes + 1 }, reference with { Sha256 = new string('0', 64) } })
        {
            var failed = await TitleCall(normal, "legacy_session_title", new LegacySessionTitleStatArgs(forged), EngineSocketJson.Default.LegacySessionTitleStatArgs);
            Assert.Equal("error", failed.GetProperty("t").GetString()); Assert.Contains("title_changed", failed.GetRawText());
        }
        _titleSessions.SetTitle(reference.SessionId, "Actual rename", "user");
        var changed = await TitleCall(bulk, "legacy_session_title_chunk", new LegacySessionTitleChunkArgs(reference, 0, 20), EngineSocketJson.Default.LegacySessionTitleChunkArgs);
        Assert.Equal("error", changed.GetProperty("t").GetString()); Assert.Contains("title_changed", changed.GetRawText());
        _f.Db.Writer.Write("test.delete_legacy", scope => scope.Execute("DELETE FROM session_info WHERE session_id=$id", new SqlArg("$id", reference.SessionId)));
        Assert.Equal("error", (await TitleCall(normal, "legacy_session_title", new LegacySessionTitleStatArgs(reference),
            EngineSocketJson.Default.LegacySessionTitleStatArgs)).GetProperty("t").GetString());
    }

    [Fact]
    public async Task Legacy_title_routes_enforce_OwnerPeer_transport_and_actual_runtime_publisher_identity()
    {
        var reference = LegacyTitle("Legacy");
        using var normal = await Open(); using var bulk = await Open(bulk: true);
        foreach (var peer in new CallPeer?[] { null, new PhonePeer("device") })
        {
            _overrideTitlePeer = true; _titlePeer = peer;
            var stat = await TitleCall(normal, "legacy_session_title", new LegacySessionTitleStatArgs(reference), EngineSocketJson.Default.LegacySessionTitleStatArgs);
            var chunk = await TitleCall(bulk, "legacy_session_title_chunk", new LegacySessionTitleChunkArgs(reference, 0, 10), EngineSocketJson.Default.LegacySessionTitleChunkArgs);
            Assert.Contains("forbidden", stat.GetRawText()); Assert.Contains("forbidden", chunk.GetRawText());
        }
        _overrideTitlePeer = false;
        Assert.Equal("error", (await TitleCall(bulk, "legacy_session_title", new LegacySessionTitleStatArgs(reference), EngineSocketJson.Default.LegacySessionTitleStatArgs)).GetProperty("t").GetString());
        Assert.Equal("error", (await TitleCall(normal, "legacy_session_title_chunk", new LegacySessionTitleChunkArgs(reference, 0, 10), EngineSocketJson.Default.LegacySessionTitleChunkArgs)).GetProperty("t").GetString());
        _f.Db.Writer.Write("test.engine_identity_changed", scope => scope.Execute("UPDATE engine_meta SET machine_id='different-engine' WHERE singleton=1"));
        var changed = await TitleCall(normal, "legacy_session_title", new LegacySessionTitleStatArgs(reference), EngineSocketJson.Default.LegacySessionTitleStatArgs);
        Assert.Contains("title_changed", changed.GetRawText());
    }

    private LegacySessionTitleRef LegacyTitle(string title)
    {
        _titleSessions.Add("legacy", "codex", "/isolated", "workbox-test"); _titleSessions.SetTitle("legacy", title, "user");
        var actual = _titleSessions.Get("legacy")!; var bytes = Encoding.UTF8.GetBytes(title);
        return new(actual.SessionId, _runtime.Machine, actual.Machine, actual.ParentSessionId, actual.TitleSource,
            actual.UpdatedAt, bytes.LongLength, SessionTitleHash.Compute(bytes));
    }

    private static async Task<JsonElement> TitleCall<T>(ClientWebSocket socket, string method, T args, JsonTypeInfo<T> type)
    {
        await Send(socket, $"{{\"t\":\"call\",\"id\":{long.MaxValue},\"feature\":\"engine\",\"method\":\"{method}\",\"args\":{JsonSerializer.Serialize(args, type)}}}");
        return await Read(socket);
    }

    private sealed class TitlePeerGate(ICallRegistry inner, EngineSocketTests fixture) : ICallRegistry
    {
        public void Read<A, R>(string feature, string method, JsonTypeInfo<A> args, JsonTypeInfo<R> result, Func<A, CallContext, Task<R>> handler, bool bulk = false) =>
            inner.Read(feature, method, args, result, (a, c) => handler(a, fixture._overrideTitlePeer ? c with { Peer = fixture._titlePeer } : c), bulk);
        public void Write<A, R>(string feature, string method, JsonTypeInfo<A> args, JsonTypeInfo<R> result, Func<A, CallContext, Task<Func<IWriteScope, R>>> handler, Func<A, string?>? order = null, Action<A, CallContext>? failed = null, bool bulk = false) => inner.Write(feature, method, args, result, handler, order, failed, bulk);
        public void Save<V>(string feature, JsonTypeInfo<V> value, Action<string, V, IWriteScope> save) => inner.Save(feature, value, save);
        public void Closed(Action<string> connection) => inner.Closed(connection);
        public void Topic<I>(string topic, JsonTypeInfo<I> item, Func<I, string> key, Func<string?, CallContext, IReadOnlyList<I>> state, bool terminal = false) => inner.Topic(topic, item, key, state, terminal);
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state, Action<string, CallContext>? opened = null) => inner.Rows(topic, state, opened);
    }
}
