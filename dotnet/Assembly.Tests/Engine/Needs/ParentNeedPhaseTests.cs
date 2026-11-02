using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mcp;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Reading;
using Assembly.Engine.Relays;

namespace Assembly.Tests.Engine.Needs;

public sealed class ParentNeedPhaseTests
{
    [Fact]
    public void Preparation_requires_generated_normalized_bytes_not_just_equal_json()
    {
        using var f = new EngineFixture();
        var (commands, reader, id) = Begin(f);
        var capture = reader.ParentNeedInput(id, default);
        var alternate = System.Text.Encoding.UTF8.GetBytes("  \"original\"  ");
        var descriptor = Descriptor(capture, alternate);
        Assert.Throws<RelayRefused>(() => PreparedParentNeedPhase.Prepare(capture, new(descriptor, alternate), default));
        Assert.Equal(ParentNeedPhase.Raw, reader.ParentNeedInput(id, default).ReadPayload().Phase);
    }

    [Fact]
    public void Unknown_survives_reopen_then_matching_final_slot_resolves_without_second_dispatch()
    {
        using var f = new EngineFixture();
        var (commands, reader, id) = Begin(f);
        var descriptor = PrepareAndDispatch(f, commands, reader, id);
        var unknown = new ParentNeedOutcome("target", "unknown", ParentNeedCodec.Identity(descriptor), null, null, null, "disconnected");
        var uncertain = PreparedParentNeedPhase.Complete(reader.ParentNeedInput(id, default), unknown, default);
        Assert.True(Complete(f, commands, uncertain).Applied);
        f.Reopen(); commands = new(f.Machine, f.Limits); reader = new(f.Db, f.Machine);
        var retained = reader.ParentNeedInput(id, default);
        Assert.Equal(ParentNeedPhase.Executing, retained.ReadPayload().Phase);
        Assert.Equal(descriptor, retained.ReadPayload().Prepared!.Descriptor);
        Assert.Null(reader.ParentNeedOutcome(id, descriptor.Source, retained.ReadPayload().InputSha256, default));
        var dispatchReplay = PreparedParentNeedPhase.Dispatch(retained, ParentNeedCodec.Identity(descriptor), default);
        Assert.True(f.Db.Writer.Write("test.dispatch-replay", s => commands.DispatchParentNeedIntent(s, dispatchReplay, 2000)).Applied);
        Assert.Equal(ParentNeedPhase.Executing, reader.ParentNeedInput(id, default).ReadPayload().Phase);
        var target = new CommandResult(true, false, null, 2, 99, 7);
        var final = new ParentNeedOutcome("target", "recorded", ParentNeedCodec.Identity(descriptor), target, null, null, null);
        var preparedFinal = PreparedParentNeedPhase.Complete(retained, final, default);
        var completed = Complete(f, commands, preparedFinal);
        Assert.True(completed.Applied, completed.Error);
        Assert.Equal("done", f.State("engine_outbox", id));
        Assert.Equal(1, f.Number("SELECT attempt FROM engine_outbox WHERE id=$id", new SqlArg("$id", id)));
        Assert.Equal(final, reader.ParentNeedOutcome(id, descriptor.Source, retained.ReadPayload().InputSha256, default));
        Assert.Equal(completed, Complete(f, commands, preparedFinal));
        var changed = PreparedParentNeedPhase.Complete(retained, final with { Result = target with { Seq = 100 } }, default);
        Assert.Equal("command_id_conflict", Complete(f, commands, changed).Error);
    }

    [Fact]
    public void Reopen_rejects_changed_answer_even_when_its_replacement_manifest_matches()
    {
        using var f = new EngineFixture();
        var (commands, reader, id) = Begin(f);
        var raw = reader.ParentNeedInput(id, default);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(raw.ReadPayload().Input.Answer!.Value, EngineJson.Default.JsonElement);
        var descriptor = Descriptor(raw, bytes);
        var prepare = PreparedParentNeedPhase.Prepare(raw, new(descriptor, bytes), default);
        Assert.True(f.Db.Writer.Write("test.prepare", s => commands.PrepareParentNeedIntent(s, prepare, 1001)).Applied);
        var persisted = reader.ParentNeedInput(id, default).ReadPayload();
        var replacement = JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.SerializeToElement("changed"), EngineJson.Default.JsonElement);
        var changed = persisted with { Prepared = new(descriptor with { Answer = new(replacement.LongLength, RelayHash.Sha(replacement)) }, replacement) };
        f.Db.Writer.Write("test.changed-prepared", s => s.Execute("UPDATE engine_outbox SET payload_json=$payload WHERE id=$id",
            new("$payload", JsonSerializer.Serialize(changed, EngineJson.Default.ParentNeedIntentPayload)), new("$id", id)));
        f.Reopen(); reader = new(f.Db, f.Machine);
        Assert.Throws<RelayRefused>(() => reader.ParentNeedInput(id, default));
        Assert.Equal(0, f.Number("SELECT attempt FROM engine_outbox WHERE id=$id", new SqlArg("$id", id)));
        Assert.Equal("pending", f.State("engine_outbox", id));
    }

    [Fact]
    public void Prepared_parent_refusal_uses_descriptor_identity_and_never_mutates_target()
    {
        using var f = new EngineFixture();
        var (commands, reader, id) = Begin(f);
        var capture = reader.ParentNeedInput(id, default);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(capture.ReadPayload().Input.Answer!.Value, EngineJson.Default.JsonElement);
        var descriptor = Descriptor(capture, bytes);
        var preparing = PreparedParentNeedPhase.Prepare(capture, new(descriptor, bytes), default);
        Assert.True(f.Db.Writer.Write("test.prepare", s => commands.PrepareParentNeedIntent(s, preparing, 1001)).Applied);
        f.Db.Writer.Write("test.epoch", s => s.Execute("UPDATE engine_session SET epoch=4 WHERE id='parent'"));
        capture = reader.ParentNeedInput(id, default);
        var outcome = new ParentNeedOutcome("source", "refused", ParentNeedCodec.Identity(descriptor), null, null, null, "stale_epoch");
        Assert.Throws<RelayRefused>(() => PreparedParentNeedPhase.Complete(capture, outcome with { IdentitySha256 = capture.ReadPayload().InputSha256 }, default));
        Assert.True(Complete(f, commands, PreparedParentNeedPhase.Complete(capture, outcome, default)).Applied);
        Assert.Equal("failed", f.State("engine_outbox", id));
        Assert.Equal(0, f.Number("SELECT attempt FROM engine_outbox WHERE id=$id", new SqlArg("$id", id)));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
    }

    [Theory]
    [InlineData("recorded_failure")]
    [InlineData("unknown_effect")]
    [InlineData("wrong_final_state")]
    public void Reopen_refuses_outcomes_that_could_not_be_committed_by_the_closed_phase_writer(string fault)
    {
        using var f = new EngineFixture();
        var (commands, reader, id) = Begin(f);
        var descriptor = PrepareAndDispatch(f, commands, reader, id);
        var current = reader.ParentNeedInput(id, default).ReadPayload();
        var status = fault == "unknown_effect" ? "unknown" : "recorded";
        var outcome = new ParentNeedOutcome("target", status, ParentNeedCodec.Identity(descriptor),
            status == "unknown" ? null : new(fault != "recorded_failure", false, fault == "recorded_failure" ? "forbidden" : null, 2, 99, 7),
            fault == "unknown_effect" ? "answer/need" : null, null, null);
        var changed = current with { Phase = status == "unknown" ? ParentNeedPhase.Executing : ParentNeedPhase.Outcome, Outcome = outcome };
        var state = status == "unknown" ? "uncertain" : fault == "wrong_final_state" ? "failed" : "done";
        f.Db.Writer.Write("test.corrupt", s => s.Execute("UPDATE engine_outbox SET state=$state,payload_json=$payload WHERE id=$id",
            new("$state", state), new("$payload", JsonSerializer.Serialize(changed, EngineJson.Default.ParentNeedIntentPayload)), new("$id", id)));
        f.Reopen(); reader = new(f.Db, f.Machine);
        Assert.Throws<RelayRefused>(() => reader.ParentNeedInput(id, default));
        Assert.Throws<RelayRefused>(() => reader.ParentNeedOutcome(id, descriptor.Source, current.InputSha256, default));
    }

    [Fact]
    public void Owning_census_recovers_after_event_loss_and_keeps_a_stable_keyset_cut()
    {
        using var f = new EngineFixture();
        var (commands, reader, firstId) = Begin(f);
        var caller = new McpCaller("parent", 3);
        for (var i = 0; i < 2; i++)
        {
            var prepared = commands.PrepareParentNeedIntent(Guid.NewGuid(), caller,
                new("need", ParentNeedAction.Answer, JsonSerializer.SerializeToElement("original")), default);
            Assert.True(f.Db.Writer.Write("test.more-intents", s => commands.BeginParentNeedIntent(s, caller, prepared, 1000)).Result.Applied);
        }
        f.Db.Writer.Write("test.event-loss", s => s.Execute("DELETE FROM engine_event"));
        f.Reopen(); commands = new(f.Machine, f.Limits); reader = new(f.Db, f.Machine);
        int FrameBytes(ParentNeedPendingPage page) => JsonSerializer.SerializeToUtf8Bytes(page, EngineJson.Default.ParentNeedPendingPage).Length;
        var page = reader.ParentNeedPending(new(Limit: 1), FrameBytes, default);
        Assert.True(page.More); Assert.NotNull(page.Cut); Assert.NotNull(page.Next);
        List<string> ids = [Assert.Single(page.Items).IntentId];
        var later = commands.PrepareParentNeedIntent(Guid.NewGuid(), caller,
            new("need", ParentNeedAction.Answer, JsonSerializer.SerializeToElement("later")), default);
        var laterId = f.Db.Writer.Write("test.after-cut", s => commands.BeginParentNeedIntent(s, caller, later, 2000)).IntentId;
        while (page.More)
        {
            var next = reader.ParentNeedPending(new(page.Next, page.Cut, 1), FrameBytes, default);
            Assert.Equal(page.Cut, next.Cut);
            ids.Add(Assert.Single(next.Items).IntentId); page = next;
        }
        Assert.Equal(3, ids.Count); Assert.Equal(3, ids.Distinct().Count());
        Assert.Contains(firstId, ids); Assert.DoesNotContain(laterId, ids);
        Assert.Equal(4, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='parent_need'"));
    }

    [Fact]
    public void Stale_prepared_dispatch_replay_has_original_result_but_never_a_second_execute_admission()
    {
        using var f = new EngineFixture();
        var (commands, reader, id) = Begin(f);
        var raw = reader.ParentNeedInput(id, default);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(raw.ReadPayload().Input.Answer!.Value, EngineJson.Default.JsonElement);
        var descriptor = Descriptor(raw, bytes);
        var prepare = PreparedParentNeedPhase.Prepare(raw, new(descriptor, bytes), default);
        Assert.True(f.Db.Writer.Write("test.prepare", s => commands.PrepareParentNeedIntent(s, prepare, 1001)).Applied);
        var original = PreparedParentNeedPhase.Dispatch(reader.ParentNeedInput(id, default), ParentNeedCodec.Identity(descriptor), default);
        var first = f.Db.Writer.Write("test.dispatch-first", s => commands.DispatchParentNeedWithAck(s, original, 1002));
        var replay = f.Db.Writer.Write("test.dispatch-stale", s => commands.DispatchParentNeedWithAck(s, original, 1003));
        Assert.True(first.Dispatched); Assert.False(replay.Dispatched); Assert.Equal(first.Result, replay.Result);
        Assert.Equal(1, f.Number("SELECT attempt FROM engine_outbox WHERE id=$id", new SqlArg("$id", id)));
    }

    [Fact]
    public void Actual_rejected_prepare_proves_Raw_source_refusal_without_persisting_target_preparation()
    {
        using var f = new EngineFixture();
        var (commands, reader, id) = Begin(f);
        f.SeedSession("child", parentId: "parent");
        var raw = reader.ParentNeedInput(id, default);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(raw.ReadPayload().Input.Answer!.Value, EngineJson.Default.JsonElement);
        var descriptor = Descriptor(raw, bytes);
        var attempted = PreparedParentNeedPhase.Prepare(raw, new(descriptor, bytes), default);
        var refused = f.Db.Writer.Write("test.prepare-refused", s => commands.PrepareParentNeedIntent(s, attempted, 1001));
        Assert.Equal("forbidden", refused.Error);
        var outcome = new ParentNeedOutcome("source", "refused", raw.ReadPayload().InputSha256, null, null, null, refused.Error);
        var completed = Complete(f, commands, PreparedParentNeedPhase.Complete(raw, outcome, default, descriptor));
        Assert.True(completed.Applied, completed.Error);
        var stored = reader.ParentNeedInput(id, default).ReadPayload();
        Assert.Equal(outcome, stored.Outcome); Assert.Null(stored.Prepared); Assert.Equal("failed", f.State("engine_outbox", id));
        var changed = descriptor with { Target = descriptor.Target with { ChildEpoch = 8 } };
        var conflict = Complete(f, commands, PreparedParentNeedPhase.Complete(raw, outcome, default, changed));
        Assert.Equal("command_id_conflict", conflict.Error);
        Assert.Equal(0, f.Number("SELECT attempt FROM engine_outbox WHERE id=$id", new SqlArg("$id", id)));
    }

    [Fact]
    public void Escalation_preparation_after_source_json_roundtrip_has_no_answer_manifest_or_bytes()
    {
        using var f = new EngineFixture(); f.SeedSession("parent", epoch: 3);
        var commands = new EngineCommands(f.Machine, f.Limits); var caller = new McpCaller("parent", 3);
        var raw = commands.PrepareParentNeedIntent(Guid.NewGuid(), caller, new("need", ParentNeedAction.Escalate, null), default);
        var accepted = f.Db.Writer.Write("test.escalation-begin", scope => commands.BeginParentNeedIntent(scope, caller, raw, 1000));
        var capture = new EngineReader(f.Db, f.Machine).ParentNeedInput(accepted.IntentId, default);
        var input = capture.ReadPayload(); Assert.Null(input.Input.Answer);
        var target = new NeedAnswerTarget("need", 1, "child", "target", 7, "local", null, "question", false,
            "parent", f.Machine, "parent", null, null);
        var normalized = input.Input with { Answer = PreparedParentNeedPhase.NormalizeAnswer(input.Input, target) };
        Assert.Null(normalized.Answer);
        var prepared = EngineCommands.PrepareParentNeed(input.Source.OperationId, normalized, target, null, default);
        Assert.Null(prepared.Manifest); Assert.Null(prepared.AnswerUtf8);
        var descriptor = new NeedAnswerRequestDescriptor(input.Source, target, prepared.Manifest, null);
        var wire = JsonSerializer.SerializeToUtf8Bytes(new ParentNeedPrepared(descriptor, prepared.AnswerUtf8), EngineJson.Default.ParentNeedPrepared);
        using var wireJson = JsonDocument.Parse(wire);
        Assert.False(wireJson.RootElement.TryGetProperty("answerUtf8", out _));
        var received = JsonSerializer.Deserialize(wire, EngineJson.Default.ParentNeedPrepared)!;
        Assert.Null(received.AnswerUtf8); Assert.Null(received.Descriptor.Answer); Assert.Equal(descriptor, received.Descriptor);
        var emptyWire = JsonSerializer.SerializeToUtf8Bytes(new ParentNeedPrepared(descriptor, []), EngineJson.Default.ParentNeedPrepared);
        var empty = JsonSerializer.Deserialize(emptyWire, EngineJson.Default.ParentNeedPrepared)!;
        Assert.NotNull(empty.AnswerUtf8); Assert.Empty(empty.AnswerUtf8);
        Assert.Throws<RelayRefused>(() => PreparedParentNeedPhase.Prepare(capture, empty, default));
        var body = new byte[] { 0, 255, 128, 42 };
        var bodyWire = JsonSerializer.SerializeToUtf8Bytes(new ParentNeedPrepared(descriptor, body), EngineJson.Default.ParentNeedPrepared);
        Assert.Equal(body, JsonSerializer.Deserialize(bodyWire, EngineJson.Default.ParentNeedPrepared)!.AnswerUtf8);
        var phase = PreparedParentNeedPhase.Prepare(capture, received, default);
        Assert.True(f.Db.Writer.Write("test.escalation-prepare", scope => commands.PrepareParentNeedIntent(scope, phase, 1001)).Applied);
    }

    private static (EngineCommands, EngineReader, string) Begin(EngineFixture f)
    {
        f.SeedSession("parent", epoch: 3);
        var commands = new EngineCommands(f.Machine, f.Limits);
        var caller = new McpCaller("parent", 3);
        var prepared = commands.PrepareParentNeedIntent(Guid.NewGuid(), caller,
            new("need", ParentNeedAction.Answer, JsonSerializer.SerializeToElement("original")), default);
        var accepted = f.Db.Writer.Write("test.begin", s => commands.BeginParentNeedIntent(s, caller, prepared, 1000));
        Assert.True(accepted.Result.Applied, accepted.Result.Error);
        return (commands, new(f.Db, f.Machine), accepted.IntentId);
    }

    private static NeedAnswerRequestDescriptor Descriptor(ParentNeedSourceCapture capture, byte[] bytes) => new(
        capture.ReadPayload().Source,
        new("need", 1, "child", "target", 7, "local", null, "question", false, "parent", capture.ReadPayload().Source.SourceMachine, "parent", null, null),
        new(bytes.LongLength, RelayHash.Sha(bytes)), null);

    private static NeedAnswerRequestDescriptor PrepareAndDispatch(EngineFixture f, EngineCommands commands, EngineReader reader, string id)
    {
        var capture = reader.ParentNeedInput(id, default);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(capture.ReadPayload().Input.Answer!.Value, EngineJson.Default.JsonElement);
        var descriptor = Descriptor(capture, bytes);
        var prepare = PreparedParentNeedPhase.Prepare(capture, new(descriptor, bytes), default);
        Assert.True(f.Db.Writer.Write("test.prepare", s => commands.PrepareParentNeedIntent(s, prepare, 1001)).Applied);
        var dispatch = PreparedParentNeedPhase.Dispatch(reader.ParentNeedInput(id, default), ParentNeedCodec.Identity(descriptor), default);
        Assert.True(f.Db.Writer.Write("test.dispatch", s => commands.DispatchParentNeedIntent(s, dispatch, 1002)).Applied);
        return descriptor;
    }

    private static CommandResult Complete(EngineFixture f, EngineCommands commands, PreparedParentNeedPhase phase) =>
        f.Db.Writer.Write("test.complete", s => commands.CompleteParentNeedIntent(s, phase, 1003));
}
