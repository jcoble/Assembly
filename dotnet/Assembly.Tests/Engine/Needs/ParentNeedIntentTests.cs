using Assembly.Engine.Reading;
using Assembly.Engine.Relays;
using Assembly.Application.Common.Database;
using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Mcp;
using Assembly.Engine.NeedsMe;

namespace Assembly.Tests.Engine.Needs;

public sealed class ParentNeedIntentTests
{
    [Fact]
    public void Begin_freezes_full_input_creates_one_sessionless_intent_and_replays_before_mutable_parent_guards()
    {
        using var f = new EngineFixture(); f.SeedSession("parent", epoch: 3);
        var caller = new McpCaller("parent", 3); var commands = new EngineCommands(f.Machine, f.Limits);
        var operation = Guid.NewGuid(); var body = new string('x', 4 * 1024 * 1024 + 1) + "\0λ";
        using var original = JsonDocument.Parse(JsonSerializer.Serialize(body));
        var prepared = commands.PrepareParentNeedIntent(operation, caller, new("need", ParentNeedAction.Answer, original.RootElement), default);
        original.Dispose();
        var accepted = f.Db.Writer.Write("test.parent-need.begin", s => commands.BeginParentNeedIntent(s, caller, prepared, 1000));
        Assert.True(accepted.Result.Applied, accepted.Result.Error);
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='parent_need' AND session_id IS NULL AND run_id IS NULL AND state='pending'"));
        var stored = JsonSerializer.Deserialize(f.Text("SELECT payload_json FROM engine_outbox WHERE kind='parent_need'")!, EngineJson.Default.ParentNeedIntentPayload)!;
        Assert.Equal(body, stored.Input.Answer!.Value.GetString());
        Assert.Equal(ParentNeedPhase.Raw, stored.Phase); Assert.Null(stored.Prepared); Assert.Null(stored.Outcome);
        Assert.Equal(f.Machine, stored.Source.SourceMachine); Assert.Equal(operation, stored.Source.OperationId);
        Assert.True(f.Text("SELECT data_json FROM engine_event WHERE kind='parent_need.requested'")!.Length < 1024);
        f.Db.Writer.Write("test.parent-need.parent-changed", s => s.Execute("UPDATE engine_session SET epoch=4,state='ended' WHERE id='parent'"));
        f.Reopen(); commands = new(f.Machine, f.Limits);
        var replay = f.Db.Writer.Write("test.parent-need.replay", s => commands.BeginParentNeedIntent(s, caller, prepared, 2000));
        Assert.Equal(accepted, replay);
        var changed = commands.PrepareParentNeedIntent(operation, caller, new("need", ParentNeedAction.Answer, JsonSerializer.SerializeToElement("changed")), default);
        var conflict = f.Db.Writer.Write("test.parent-need.conflict", s => commands.BeginParentNeedIntent(s, caller, changed, 3000));
        Assert.Equal("command_id_conflict", conflict.Result.Error);
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='parent_need'"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_event WHERE kind='parent_need.requested'"));
    }

    [Fact]
    public void Begin_rechecks_actual_source_parent_before_creating_any_durable_intent()
    {
        using var f = new EngineFixture(); f.SeedSession("parent", epoch: 3);
        var caller = new McpCaller("parent", 3); var commands = new EngineCommands(f.Machine, f.Limits);
        var prepared = commands.PrepareParentNeedIntent(Guid.NewGuid(), caller, new("need", ParentNeedAction.Escalate, null), default);
        f.Db.Writer.Write("test.parent-need.reserve", s => s.Execute("UPDATE engine_session SET reserved_epoch=4 WHERE id='parent'"));
        var result = f.Db.Writer.Write("test.parent-need.begin", s => commands.BeginParentNeedIntent(s, caller, prepared, 1000));
        Assert.Equal("forbidden", result.Result.Error);
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='parent_need'"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_event WHERE kind='parent_need.requested'"));
    }
    [Fact]
    public void Prepare_freezes_original_bytes_and_dispatch_is_durable_replay_safe_and_never_resets_executing()
    {
        using var f = new EngineFixture(); f.SeedSession("parent", epoch: 3);
        var caller = new McpCaller("parent", 3); var commands = new EngineCommands(f.Machine, f.Limits);
        var reader = new EngineReader(f.Db, f.Machine);
        var input = JsonSerializer.SerializeToElement("original");
        var raw = commands.PrepareParentNeedIntent(Guid.NewGuid(), caller, new("need", ParentNeedAction.Answer, input), default);
        var begin = f.Db.Writer.Write("test.intent.begin", scope => commands.BeginParentNeedIntent(scope, caller, raw, 1000));
        var capture = reader.ParentNeedInput(begin.IntentId, default);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(input);
        var target = new NeedAnswerTarget("need", 1, "child", "target", 7, "local", null,
            "question", false, "parent", f.Machine, "parent", null, null);
        var descriptor = new NeedAnswerRequestDescriptor(capture.ReadPayload().Source, target,
            new(bytes.LongLength, RelayHash.Sha(bytes)), null);
        var prepared = PreparedParentNeedPhase.Prepare(capture, new(descriptor, bytes), default);
        bytes[0] = 0;
        var first = f.Db.Writer.Write("test.intent.prepare", scope => commands.PrepareParentNeedIntent(scope, prepared, 1001));
        Assert.True(first.Applied, first.Error);
        var persisted = reader.ParentNeedInput(begin.IntentId, default).ReadPayload();
        Assert.Equal("\"original\"", System.Text.Encoding.UTF8.GetString(persisted.Prepared!.AnswerUtf8!));
        var dispatch = PreparedParentNeedPhase.Dispatch(reader.ParentNeedInput(begin.IntentId, default), ParentNeedCodec.Identity(descriptor), default);
        var sent = f.Db.Writer.Write("test.intent.dispatch", scope => commands.DispatchParentNeedIntent(scope, dispatch, 1002));
        Assert.True(sent.Applied, sent.Error);
        f.Reopen(); commands = new(f.Machine, f.Limits); reader = new(f.Db, f.Machine);
        Assert.Equal(ParentNeedPhase.Executing, reader.ParentNeedInput(begin.IntentId, default).ReadPayload().Phase);
        Assert.Equal("uncertain", f.State("engine_outbox", begin.IntentId));
        Assert.Equal(1, f.Number("SELECT attempt FROM engine_outbox WHERE kind='parent_need'"));
        f.Db.Writer.Write("test.parent.changed", scope => scope.Execute("UPDATE engine_session SET epoch=4,state='ended' WHERE id='parent'"));
        Assert.Equal(first, f.Db.Writer.Write("test.prepare.replay", scope => commands.PrepareParentNeedIntent(scope, prepared, 2000)));
        Assert.Equal(sent, f.Db.Writer.Write("test.dispatch.replay", scope => commands.DispatchParentNeedIntent(scope, dispatch, 2001)));
        Assert.Equal(ParentNeedPhase.Executing, reader.ParentNeedInput(begin.IntentId, default).ReadPayload().Phase);
        Assert.Equal(1, f.Number("SELECT attempt FROM engine_outbox WHERE kind='parent_need'"));
    }

    [Fact]
    public void Raw_source_refusal_binds_original_input_without_claiming_any_target_result_or_effect()
    {
        using var f = new EngineFixture(); f.SeedSession("parent", epoch: 3);
        var caller = new McpCaller("parent", 3); var commands = new EngineCommands(f.Machine, f.Limits);
        var raw = commands.PrepareParentNeedIntent(Guid.NewGuid(), caller, new("need", ParentNeedAction.Escalate, null), default);
        var begin = f.Db.Writer.Write("test.raw.begin", scope => commands.BeginParentNeedIntent(scope, caller, raw, 1000));
        var reader = new EngineReader(f.Db, f.Machine); var capture = reader.ParentNeedInput(begin.IntentId, default);
        var outcome = new ParentNeedOutcome("source", "refused", capture.ReadPayload().InputSha256, null, null, null, "session_gone");
        var prepared = PreparedParentNeedPhase.Complete(capture, outcome, default);
        f.Db.Writer.Write("test.parent.ended", scope => scope.Execute("UPDATE engine_session SET state='ended' WHERE id='parent'"));
        var result = f.Db.Writer.Write("test.raw.refused", scope => commands.CompleteParentNeedIntent(scope, prepared, 1001));
        Assert.True(result.Applied, result.Error);
        var stored = reader.ParentNeedInput(begin.IntentId, default).ReadPayload();
        Assert.Equal(ParentNeedPhase.Outcome, stored.Phase); Assert.Null(stored.Prepared);
        Assert.Equal(outcome, stored.Outcome); Assert.Equal("failed", f.State("engine_outbox", begin.IntentId));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
        Assert.Equal(result, f.Db.Writer.Write("test.raw.replay", scope => commands.CompleteParentNeedIntent(scope, prepared, 2000)));
    }

}
