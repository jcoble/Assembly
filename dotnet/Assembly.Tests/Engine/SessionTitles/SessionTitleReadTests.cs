using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Mirror;
using Assembly.Engine.Reading;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Engine.SessionTitles;

public sealed class SessionTitleReadTests
{
    private static readonly EngineActor System = new(ActorKind.System, "title-test");
    private static CommandResult Title(EngineFixture f, string value, long version, Guid? id = null)
    {
        var prepared = EngineCommands.PrepareSessionTitle(new ReportSessionTitle(value));
        return f.Db.Writer.Write("title", s => new EngineCommands(f.Machine, f.Limits).ApplyPreparedSessionTitle(s,
            new(id ?? Guid.NewGuid(), f.Machine, System, "s", version, 0, 1000), prepared));
    }

    [Fact]
    public void Stat_chunks_reopen_full_unicode_and_nul_then_refuse_every_session_revision_change()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var title = "\0" + new string('x', 1048574) + "😀\0end";
        Assert.True(Title(f, title, 0).Applied); f.Reopen();
        var reader = new EngineReader(f.Db, f.Machine);
        var stat = reader.SessionTitle(new("s"), default);
        var exact = new UTF8Encoding(false, true).GetBytes(title);
        Assert.Equal(f.Machine, stat.EngineMachine); Assert.Equal(exact.Length, stat.Utf8Bytes);
        var expected = new byte["session-title/v1"u8.Length + 8 + exact.Length];
        "session-title/v1"u8.CopyTo(expected);
        BinaryPrimitives.WriteUInt64BigEndian(expected.AsSpan("session-title/v1"u8.Length, 8), (ulong)exact.Length);
        exact.CopyTo(expected, "session-title/v1"u8.Length + 8);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), stat.Sha256);
        using var all = new MemoryStream(); long offset = 0;
        do
        {
            var chunk = reader.SessionTitleChunk(new("s", stat.Version, stat.Epoch, stat.Utf8Bytes, offset, Machine: stat.EngineMachine), default);
            Assert.Equal(offset, chunk.Offset); all.Write(chunk.Data);
            if (chunk.NextOffset is not { } next) break;
            Assert.Equal(offset + chunk.Data.LongLength, next); offset = next;
        } while (true);
        Assert.Equal(exact, all.ToArray()); Assert.Equal(title, new UTF8Encoding(false, true).GetString(all.ToArray()));
        Assert.Empty(reader.SessionTitleChunk(new("s", stat.Version, 0, stat.Utf8Bytes, stat.Utf8Bytes), default).Data);
        Assert.Throws<InvalidOperationException>(() => reader.SessionTitleChunk(new("s", stat.Version, 0, stat.Utf8Bytes - 1, 0), default));
        Assert.Throws<InvalidOperationException>(() => reader.SessionTitle(new("s", "paired-phone-id"), default));
        Assert.Throws<ArgumentException>(() => reader.SessionTitleChunk(new("s", stat.Version, 0, stat.Utf8Bytes, 0, 1048577), default));
        Assert.True(f.Apply(new ReportCapabilities(true), "s", stat.Version, actor: System).Applied);
        Assert.Equal("title_changed", Assert.Throws<InvalidOperationException>(() => reader.SessionTitleChunk(new("s", stat.Version, 0, stat.Utf8Bytes, 0), default)).Message);
        Assert.True(Title(f, "other", stat.Version + 1).Applied);
        Assert.True(Title(f, title, stat.Version + 2).Applied);
        Assert.Throws<InvalidOperationException>(() => reader.SessionTitleChunk(new("s", stat.Version, 0, stat.Utf8Bytes, 0), default));
    }

    [Fact]
    public void Prepared_title_replays_original_context_and_small_bytes_and_conflicts_after_hidden_suffix_change()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var id = Guid.NewGuid(); var text = new string('x', 40000) + "\0A";
        var first = Title(f, text, 0, id); Assert.True(first.Applied);
        Assert.Equal(first, Title(f, text, 0, id));
        Assert.Equal("command_id_conflict", Title(f, text + "B", 0, id).Error);
        Assert.Equal(1, f.Count("engine_event"));
        Assert.True(Title(f, text, 1).Applied); Assert.Equal(1, f.Count("engine_event"));
        var small = new ReportSessionTitle("Small"); var context = new CommandContext(Guid.NewGuid(), f.Machine, System, "s", 1, 0, 2000);
        var prepared = EngineCommands.PrepareSessionTitle(small);
        var accepted = f.Db.Writer.Write("title", s => new EngineCommands(f.Machine, f.Limits).ApplyPreparedSessionTitle(s, context, prepared));
        Assert.True(accepted.Applied); Assert.Equal(accepted, f.Apply(context, small));
        f.Reopen(); Assert.Equal(accepted, f.Apply(context, small));
    }

    [Fact]
    public void Entity_snapshot_and_actual_mirror_receive_bounded_explicit_full_body_and_label_previews()
    {
        using var f = new EngineFixture(); using var mirror = new EngineFixture(); f.SeedSession(); f.SeedSession("to", state: "running");
        var title = string.Concat(Enumerable.Repeat("😀\0\"\\", 20000)); Assert.True(Title(f, title, 0).Applied);
        var body = new string('x', 40000) + "\0TASK_SUFFIX";
        var prompt = PreparedHelperPrompt.Prepare(new("s", "to", body, "prompt"), title, 0);
        Assert.True(f.Db.Writer.Write("helper", s => new EngineCommands(f.Machine, f.Limits).ApplyPreparedHelperPrompt(s,
            new(Guid.NewGuid(), f.Machine, new(ActorKind.Session, "s"), "m", 0, 0, 1000), prompt)).Applied);
        var reader = new EngineReader(f.Db, f.Machine);
        var session = reader.Entity("session", "s", default)!.Value;
        Assert.Equal(256, session.GetProperty("title").GetString()!.EnumerateRunes().Count()); Assert.False(session.GetProperty("titleComplete").GetBoolean());
        var message = reader.Entity("message", "m", default)!.Value;
        Assert.Equal(new string('x', 256), message.GetProperty("text").GetString()); Assert.Equal(0, message.GetProperty("textComplete").GetInt32());
        Assert.False(message.GetProperty("senderLabelComplete").GetBoolean());
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(message, EngineJson.Default.JsonElement).Length <= 32768);
        mirror.Db.Writer.Write("open", s => MirrorApply.Open(s, f.Machine, "workbox-test", 1000));
        mirror.Db.Writer.Write("begin", s => MirrorApply.BeginSnapshot(s, f.Machine, 1000));
        using var snapshot = reader.Snapshot("mirror", null, default); SnapshotPage page;
        do
        {
            page = snapshot.ReadNext();
            mirror.Db.Writer.Write("page", s => { MirrorApply.SnapshotPage(s, f.Machine, page.Items); return 0; });
        } while (page.HasMore);
        mirror.Db.Writer.Write("end", s => MirrorApply.EndSnapshot(s, f.Machine, page.Through, page.At, 1000));
        Assert.Equal(message.GetRawText(), new EngineReader(mirror.Db, mirror.Machine).Entity("message", "m", default, f.Machine)!.Value.GetRawText());
        Assert.Equal(Encoding.UTF8.GetBytes(body), f.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message WHERE id='m'", r => r.Blob(0), default));
    }

    [Theory]
    [InlineData("sessions", 65536)]
    [InlineData("mirror", 1048576)]
    public void Snapshot_measures_actual_complete_frame_and_keeps_last_included_key(string topic, int budget)
    {
        using var f = new EngineFixture();
        for (var i = 0; i < 70; i++) f.SeedSession($"s{i:D3}");
        f.Db.Writer.Write("metadata", s => s.Execute("UPDATE engine_session SET folder=$folder", new Assembly.Application.Common.Database.SqlArg("$folder", new string('x', 20000))));
        var reader = new EngineReader(f.Db, f.Machine); var id = long.MaxValue;
        var source = f.Machine + new string('m', 15000) + "\"\\😀";
        int Size(SnapshotPage p) => JsonSerializer.SerializeToUtf8Bytes(new EngineSnapshotFrame(id, source, p.Through, p.Items, p.HasMore, p.At), EngineSocketJson.Default.EngineSnapshotFrame).Length;
        using var snapshot = reader.Snapshot(topic, null, default); List<string> ids = []; bool more;
        do
        {
            var measurements = 0;
            var page = snapshot.ReadNext(candidate => { measurements++; return Size(candidate); });
            Assert.InRange(measurements, 1, 8); Assert.InRange(Size(page), 1, budget); more = page.HasMore;
            ids.AddRange(page.Items.Select(i => i.GetProperty("id").GetString()!));
        } while (more);
        Assert.Equal(Enumerable.Range(0, 70).Select(i => $"s{i:D3}"), ids);
        using var refuse = reader.Snapshot(topic, null, default);
        Assert.Throws<InvalidOperationException>(() => refuse.ReadNext(_ => budget + 1));
    }
    [Theory]
    [InlineData("sessions", 65536)]
    [InlineData("mirror", 1048576)]
    public void Actual_snapshot_encoder_exact_limit_and_one_extra_byte_preserve_final_more_and_cursor(string topic, int budget)
    {
        using var f = new EngineFixture(); f.SeedSession("a"); f.SeedSession("b");
        var reader = new EngineReader(f.Db, f.Machine);
        var items = new[] { reader.Entity("session", "a", default)!.Value, reader.Entity("session", "b", default)!.Value };
        var source = f.Machine + "\"\\😀";
        int Size(SnapshotPage page) => JsonSerializer.SerializeToUtf8Bytes(new EngineSnapshotFrame(long.MaxValue, source,
            page.Through, page.Items, page.HasMore, page.At), EngineSocketJson.Default.EngineSnapshotFrame).Length;
        var complete = new SnapshotPage(0, items, false, null);
        source += new string('z', budget - Size(complete));
        Assert.Equal(budget, Size(complete));
        using (var exact = reader.Snapshot(topic, null, default))
        {
            var page = exact.ReadNext(Size); Assert.False(page.HasMore); Assert.Equal(2, page.Items.Count); Assert.Equal(budget, Size(page));
        }
        source += "x";
        using var split = reader.Snapshot(topic, null, default);
        var first = split.ReadNext(Size); Assert.True(first.HasMore); Assert.Single(first.Items); Assert.True(Size(first) <= budget);
        var last = split.ReadNext(Size); Assert.False(last.HasMore); Assert.Single(last.Items); Assert.True(Size(last) <= budget);
        Assert.Equal(new[] { "a", "b" }, first.Items.Concat(last.Items).Select(i => i.GetProperty("id").GetString()));
    }

}
