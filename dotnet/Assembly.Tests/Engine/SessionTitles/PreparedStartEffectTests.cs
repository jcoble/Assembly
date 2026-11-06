using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.SessionTitles;

public sealed class PreparedStartEffectTests
{
    [Theory]
    [InlineData("outbox")]
    [InlineData("receipt")]
    [InlineData("projection")]
    public void Prepared_Start_faults_roll_back_child_effect_event_and_receipt_together(string boundary)
    {
        using var f = new EngineFixture();
        if (boundary != "projection")
            f.Db.Writer.Write("test.fault", s => s.Execute(boundary == "outbox"
                ? "CREATE TRIGGER fail_start BEFORE INSERT ON engine_outbox BEGIN SELECT RAISE(ABORT,'outbox'); END"
                : "CREATE TRIGGER fail_start BEFORE INSERT ON engine_command_receipt BEGIN SELECT RAISE(ABORT,'receipt'); END"));
        var commands = new EngineCommands(f.Machine, f.Limits, projectStart: boundary == "projection"
            ? (_, _, _) => throw new InvalidOperationException("projection") : null);
        var prepared = EngineCommands.PrepareSessionTitle(new StartSession("codex", null, "/folder", "T\0😀suffix", null, null, null));
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "owner"), "child", 0, 0, 1000);
        Assert.ThrowsAny<Exception>(() => { f.Db.Writer.Write("test.faulted_start", s => commands.ApplyPreparedSessionTitle(s, context, prepared)); });
        Assert.Equal(0, f.Count("engine_session")); Assert.Equal(0, f.Count("engine_outbox"));
        Assert.Equal(0, f.Count("engine_command_receipt")); Assert.Empty(f.Events());
    }

    [Fact]
    public void Misbound_Start_token_throws_before_any_child_mutation()
    {
        using var f = new EngineFixture();
        var prepared = EngineCommands.PrepareSessionTitle(new StartSession("codex", null, "/folder", "Title", null, null, null));
        var other = (StartSession)prepared.Command with { Title = "other" };
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "owner"), "child", 0, 0, 1000);
        Assert.Throws<InvalidOperationException>(() =>
        {
            f.Db.Writer.Write("test.misbound", s => SessionTransitions.ApplyPreparedTitle(s, context, other, 1, 5, "", prepared.TitleUtf8,
                startEffectPayload: prepared.StartEffectPayload));
        });
        Assert.Equal(0, f.Count("engine_session")); Assert.Equal(0, f.Count("engine_outbox")); Assert.Empty(f.Events());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void Frozen_start_token_keeps_original_typed_payload_and_refuses_another_instance(bool? managed)
    {
        var original = new StartSession("codex", "model", "/folder", "T\0😀suffix", "parent", "run", "https://task", "https://repo", managed);
        var prepared = EngineCommands.PrepareSessionTitle(original);
        var frozen = Assert.IsType<StartSession>(prepared.Command); Assert.NotSame(original, frozen);
        var payload = Assert.IsType<PreparedStartEffectPayload>(prepared.StartEffectPayload);
        Assert.Equal(JsonSerializer.Serialize(original, EngineJson.Default.StartSession), payload.ForExact(frozen).GetRawText());
        Assert.Throws<InvalidOperationException>(() => { payload.ForExact(frozen with { }); });
        Assert.Equal(Encoding.UTF8.GetBytes(original.Title), prepared.TitleUtf8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prepared_and_ordinary_Start_keep_exact_original_outbox_bytes_and_single_effect(bool large)
    {
        using var f = new EngineFixture(); f.SeedSession("parent"); f.SeedRun("run", "parent");
        var title = "T\0😀" + (large ? new string('界', 1500000) : "small") + "suffix\0";
        var original = new StartSession("codex", null, "/folder", title, "parent", "run", "https://task", "https://repo", false);
        var prepared = EngineCommands.PrepareSessionTitle(original);
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "owner"), "prepared", 0, 0, 1000);
        var expected = JsonSerializer.SerializeToUtf8Bytes(original, EngineJson.Default.StartSession);
        var commands = new EngineCommands(f.Machine, f.Limits);
        var result = f.Db.Writer.Write("test.prepared_start", s => commands.ApplyPreparedSessionTitle(s, context, prepared));
        Assert.True(result.Applied, result.Error);
        Assert.Equal(expected, f.Db.QueryFirst("SELECT CAST(payload_json AS BLOB) FROM engine_outbox WHERE id=$id", r => r.Blob(0), default, new SqlArg("$id", context.CommandId.ToString("N") + "-0")));
        Assert.Equal(Encoding.UTF8.GetBytes(title), f.Db.QueryFirst("SELECT CAST(title AS BLOB) FROM engine_session WHERE id='prepared'", r => r.Blob(0), default));
        Assert.Equal(result.Seq, f.Number("SELECT event_seq FROM engine_outbox WHERE id=$id", new SqlArg("$id", context.CommandId.ToString("N") + "-0")));
        Assert.Equal(result, f.Db.Writer.Write("test.replay_start", s => commands.ApplyPreparedSessionTitle(s, context with { NowMs = 9000 }, prepared)));
        Assert.Equal(1, f.Count("engine_outbox"));
        if (!large)
        {
            Assert.True(f.Apply(context with { EntityId = "ordinary", CommandId = Guid.NewGuid() }, original).Applied);
            Assert.Equal(expected, f.Db.QueryFirst("SELECT CAST(payload_json AS BLOB) FROM engine_outbox WHERE session_id='ordinary'", r => r.Blob(0), default));
        }
    }

    [Theory]
    [InlineData(null, "{\"provider\":\"codex\",\"model\":null,\"folder\":\"/folder\",\"title\":\"Title\",\"parentId\":null,\"runId\":null,\"taskUrl\":null}")]
    [InlineData(false, "{\"provider\":\"codex\",\"model\":null,\"folder\":\"/folder\",\"title\":\"Title\",\"parentId\":null,\"runId\":null,\"taskUrl\":null,\"managedCheckout\":false}")]
    public void Typed_start_payload_goldens_keep_omissions_distinct_from_command_canonical(bool? managed, string golden)
    {
        var prepared = EngineCommands.PrepareSessionTitle(new StartSession("codex", null, "/folder", "Title", null, null, null, ManagedCheckout: managed));
        Assert.Equal(golden, prepared.StartEffectPayload!.ForExact((StartSession)prepared.Command).GetRawText());
        Assert.StartsWith("{\"kind\":\"start_session\",", prepared.CanonicalJournal);
    }
}
