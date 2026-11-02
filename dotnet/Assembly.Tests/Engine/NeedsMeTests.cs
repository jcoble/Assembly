using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Sessions;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine;

public sealed class NeedsMeTests
{
    private static readonly EngineActor Adapter = new(ActorKind.System, "adapter");
    private static readonly JsonElement Allow = JsonSerializer.SerializeToElement("allow");

    [Fact]
    public void Two_screens_answer_once()
    {
        using var f = new EngineFixture(); f.SeedSession("s");
        Assert.True(OpenProvider(f, new OpenNeed("s", null, null, "permission", "Allow?", ["allow"],
            "request-7", "chat-row-2", 2000), "n").Applied);
        var first = AnswerOwner(f, "n", Allow);
        var second = f.Apply(new ResolveNeed("answered", Allow), "n", version: 2);
        Assert.True(first.Applied); Assert.Equal("already_resolved", second.Error);
        Assert.Equal("answered", f.State("engine_needs_me", "n"));
        Assert.Equal("request-7", Field(f, "provider_request_id"));
        Assert.Equal(1, f.Count("engine_outbox"));
        Assert.Equal("answer/n", f.Text("SELECT id FROM engine_outbox"));
        Assert.Contains(f.Events(), e => e.Kind == "needs_me.answered");
        f.Reopen();
        Assert.Equal("already_resolved", f.Apply(new ResolveNeed("answered", Allow), "n", version: 2).Error);
        Assert.Equal(1, f.Count("engine_outbox"));
    }

    [Fact]
    public void Timeout_permission_cancels_question_waits()
    {
        using var f = RunningStep();
        Assert.True(f.Apply(new OpenNeed("s", "r", "step", "question", "Secret question", [], null, null, 2000), "q", actor: Adapter).Applied);
        SeedLegacy(f, new OpenNeed("s", "r", null, "permission", "Allow?", [], "request-7", null, 2000), "n");
        Assert.Equal(2000, NeedTransitions.NextDeadline(f.Db));
        f.Time.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(1999));
        Assert.False(Expire(f, "n").Applied); Assert.False(Expire(f, "q").Applied);
        Assert.Equal("open", f.State("engine_needs_me", "n")); Assert.Equal(0, f.Count("engine_outbox"));
        f.Time.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(2000));
        Assert.True(Expire(f, "n").Applied); Assert.True(Expire(f, "q").Applied);
        Assert.Equal("expired", f.State("engine_needs_me", "n")); Assert.Equal("expired", f.State("engine_needs_me", "q"));
        Assert.Null(Field(f, "answer_json")); Assert.Null(Field(f, "answer_json", "q"));
        Assert.Equal("waiting", f.State("engine_step", "step"));
        Assert.Equal("waiting", f.Text("SELECT state FROM engine_step_attempt"));
        Assert.Equal(1, f.Count("engine_outbox")); Assert.Null(NeedTransitions.NextDeadline(f.Db));
        using var payload = JsonDocument.Parse(f.Text("SELECT payload_json FROM engine_outbox")!);
        Assert.Equal("request-7", payload.RootElement.GetProperty("providerRequestId").GetString());
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("answer").ValueKind);
        Assert.False(payload.RootElement.TryGetProperty("binding", out _)); Assert.False(payload.RootElement.TryGetProperty("cancellation", out _));
        Assert.DoesNotContain("Secret question", f.Text("SELECT group_concat(data_json) FROM engine_event")!);
    }

    [Fact]
    public void Last_open_need_restores_state()
    {
        using var f = RunningStep();
        foreach (var id in new[] { "a", "b" })
            Assert.True(f.Apply(new OpenNeed("s", "r", "step", "question", "Question", [], null, null, null), id, actor: Adapter).Applied);
        Assert.Equal("needs_me", f.State("engine_session", "s")); Assert.Equal("needs_me", f.State("engine_run", "r"));
        Assert.True(f.Apply(new ResolveNeed("answered", Allow), "a", version: 1).Applied);
        Assert.Equal("needs_me", f.State("engine_session", "s")); Assert.Equal("needs_me", f.State("engine_run", "r"));
        Assert.True(f.Apply(new ResolveNeed("answered", Allow), "b", version: 1).Applied);
        Assert.Equal("running", f.State("engine_session", "s")); Assert.Equal("running", f.State("engine_run", "r"));
        Assert.Equal("waiting", f.State("engine_step", "step"));
        Assert.Equal(1, f.Events().Count(e => e.Kind == "step.changed"));
    }

    [Fact]
    public void Same_request_preserves_chat_link()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var open = ProviderCard(new OpenNeed("s", null, null, "permission", "Allow?", ["allow"], "request-7", "chat-row-2", null));
        Assert.True(OpenProvider(f, open, "n").Applied);
        var events = f.Count("engine_event");
        Assert.True(f.Apply(open, "duplicate", actor: Adapter).Applied);
        Assert.Equal(1, f.Count("engine_needs_me")); Assert.Equal(events, f.Count("engine_event"));
        Assert.Equal("chat-row-2", Field(f, "chat_row_id"));
        Assert.Equal("provider_request_conflict", f.Apply(open with { Text = "Different" }, "other", actor: Adapter).Error);
        Assert.Equal("provider_request_conflict", f.Apply(open with { ChatRowId = "other" }, "other", actor: Adapter).Error);
    }

    [Theory]
    [InlineData("answered", ActorKind.Window, true)]
    [InlineData("dismissed", ActorKind.Window, true)]
    [InlineData("expired", ActorKind.System, true)]
    [InlineData("cancelled", ActorKind.System, true)]
    [InlineData("answered", ActorKind.System, true)]
    [InlineData("dismissed", ActorKind.System, false)]
    [InlineData("expired", ActorKind.Window, false)]
    [InlineData("cancelled", ActorKind.Window, false)]
    [InlineData("answered", ActorKind.Session, false)]
    public void Resolution_edges_record_one_actor_and_time(string state, ActorKind actor, bool allowed)
    {
        using var f = new EngineFixture(); f.SeedSession();
        Assert.True(OpenProvider(f, new OpenNeed("s", null, null, "permission", "Allow?", ["allow"], "request", "chat", 2000), "n").Applied);
        f.Time.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(2000));
        var result = state == "answered" && actor == ActorKind.Window ? AnswerOwner(f, "n", Allow)
            : f.Apply(new ResolveNeed(state, state == "answered" ? Allow : null, actor == ActorKind.System ? "policy_or_cancel" : null), "n", version: 1, actor: new(actor, "resolver"));
        var resolver = state == "answered" && actor == ActorKind.Window ? "owner" : "resolver";
        Assert.Equal(allowed, result.Applied);
        if (!allowed) { Assert.Equal("forbidden", result.Error); Assert.Null(Field(f, "answered_by")); return; }
        Assert.Equal(resolver, Field(f, "answered_by")); Assert.Equal(2000, f.Number("SELECT answered_ms FROM engine_needs_me"));
        Assert.Equal(state != "answered" ? null : actor == ActorKind.Window ? "{\"optionId\":\"allow\"}" : "\"allow\"", Field(f, "answer_json"));
        Assert.Equal("already_resolved", f.Apply(new ResolveNeed("answered", Allow), "n", version: 2).Error);
        Assert.Equal(resolver, Field(f, "answered_by")); Assert.Equal(state == "answered" && actor == ActorKind.System ? 0 : 1, f.Count("engine_outbox"));
    }

    private static EngineFixture RunningStep()
    {
        var f = new EngineFixture(); f.SeedSession();
        Assert.True(f.Apply(new SetSessionState("running", null, null), "s", actor: Adapter).Applied);
        f.SeedRun(state: "running"); f.SeedStep(state: "running", sessionId: "s", attempt: 1);
        f.Db.Writer.Write("test.attempt", s => s.Execute("INSERT INTO engine_step_attempt(step_id,attempt,state,started_ms,late) VALUES('step',1,'running',1000,0)"));
        return f;
    }

    [Fact]
    public void Need_commands_round_trip_through_dispatch_json()
    {
        EngineCommand open = new OpenNeed("s", null, null, "choice", "Private question", ["One", "Two"], "request", "chat", 2000);
        var json = JsonSerializer.Serialize(open, EngineJson.Default.EngineCommand);
        var decoded = Assert.IsType<OpenNeed>(JsonSerializer.Deserialize(json, EngineJson.Default.EngineCommand));
        Assert.Equal("choice", decoded.Kind); Assert.Equal(new[] { "One", "Two" }, decoded.Options);
        EngineCommand resolve = new ResolveNeed("answered", Allow);
        var answer = Assert.IsType<ResolveNeed>(JsonSerializer.Deserialize(
            JsonSerializer.Serialize(resolve, EngineJson.Default.EngineCommand), EngineJson.Default.EngineCommand));
        Assert.Equal("allow", answer.Answer!.Value.GetString());
    }

    [Fact]
    public void Validation_and_fencing_leave_no_domain_changes()
    {
        using var f = RunningStep();
        var open = new OpenNeed("s", "r", "step", "question", "Question", [], null, null, null);
        var before = f.Count("engine_event");
        foreach (var bad in new[] { open with { Kind = "unknown" }, open with { Text = " " },
            open with { Options = [" "] }, open with { Options = ["same", "same"] },
            open with { Kind = "choice" }, open with { RunId = null }, open with { RunId = "missing" },
            open with { StepId = "missing" }, open with { SessionId = "missing" } })
            Assert.Equal("bad_args", f.Apply(bad, "n", actor: Adapter).Error);
        Assert.Equal("forbidden", f.Apply(open, "n", actor: new(ActorKind.Session, "stranger")).Error);
        Assert.Equal("stale_epoch", f.Apply(open, "n", epoch: 1, actor: Adapter).Error);
        Assert.Equal("version_conflict", f.Apply(open, "n", version: 1, actor: Adapter).Error);
        Assert.Equal(0, f.Count("engine_needs_me")); Assert.Equal(before, f.Count("engine_event"));
        Assert.True(f.Apply(open, "n", actor: Adapter).Applied);
        Assert.Equal("bad_args", f.Apply(new ResolveNeed("answered", null), "n", version: 1).Error);
        Assert.Equal("version_conflict", f.Apply(new ResolveNeed("answered", Allow), "n").Error);
        Assert.Equal("stale_epoch", f.Apply(new ResolveNeed("answered", Allow), "n", version: 1, epoch: 1).Error);
        Assert.Equal("open", f.State("engine_needs_me", "n")); Assert.Null(Field(f, "answer_json"));
    }

    [Theory]
    [InlineData("question")]
    [InlineData("choice")]
    [InlineData("confirmation")]
    public void Nonpermission_expiry_never_answers(string kind)
    {
        using var f = new EngineFixture(); f.SeedSession();
        Assert.True(f.Apply(new OpenNeed("s", null, null, kind, "Private text", ["Private A", "Private B"], null, null, 1000), "n", actor: Adapter).Applied);
        Assert.True(Expire(f, "n").Applied);
        Assert.Null(Field(f, "answer_json")); Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_message WHERE id='need-answer/n'"));
        Assert.DoesNotContain("Private", f.Text("SELECT group_concat(data_json) FROM engine_event")!);
        Assert.Equal("idle", f.State("engine_session", "s"));
    }

    [Fact]
    public void Live_activity_changes_under_needs_and_is_restored()
    {
        using var f = new EngineFixture(); f.SeedSession();
        Assert.True(f.Apply(new OpenNeed("s", null, null, "question", "Question", [], null, null, null), "n", actor: Adapter).Applied);
        Assert.True(f.Apply(new SetSessionState("running", null, null), "s", version: 1, actor: Adapter).Applied);
        Assert.Equal("needs_me", f.State("engine_session", "s"));
        Assert.True(f.Apply(new ResolveNeed("answered", Allow), "n", version: 1).Applied);
        Assert.Equal("running", f.State("engine_session", "s"));
    }

    [Fact]
    public void Answer_effect_failure_rolls_back_answer_derived_states_and_events()
    {
        using var f = RunningStep();
        Assert.True(OpenProvider(f, new OpenNeed("s", "r", "step", "permission", "Private question", ["allow", "Private option"], "request", "chat", null), "n").Applied);
        var events = f.Count("engine_event"); var receipts = f.Count("engine_command_receipt");
        var next = f.Number("SELECT next_seq FROM engine_meta");
        f.Db.Writer.Write("test.fail-answer", s => s.Execute("CREATE TRIGGER fail_answer AFTER INSERT ON engine_outbox BEGIN SELECT RAISE(ABORT,'injected'); END"));
        Assert.Throws<SqliteException>(() => AnswerOwner(f, "n", Allow));
        Assert.Equal("open", f.State("engine_needs_me", "n")); Assert.Null(Field(f, "answer_json"));
        Assert.Null(Field(f, "answered_by")); Assert.Null(Field(f, "answered_ms"));
        Assert.Equal("needs_me", f.State("engine_session", "s")); Assert.Equal("needs_me", f.State("engine_run", "r"));
        Assert.Equal(events, f.Count("engine_event")); Assert.Equal(receipts, f.Count("engine_command_receipt"));
        Assert.Equal(next, f.Number("SELECT next_seq FROM engine_meta")); Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Fact]
    public void Answer_and_option_texts_stay_out_of_events_and_replay_is_durable()
    {
        using var f = new EngineFixture(); f.SeedSession();
        Assert.True(OpenProvider(f, new OpenNeed("s", null, null, "permission", "Private question", ["Private A", "Private B"], "request", "chat", null), "n").Applied);
        var answer = JsonSerializer.SerializeToElement("Private A");
        var id = Guid.NewGuid();
        var result = AnswerOwner(f, "n", answer, id);
        Assert.True(result.Applied);
        f.Reopen();
        Assert.Equal(result, AnswerOwner(f, "n", answer, id));
        Assert.Equal(1, f.Count("engine_outbox"));
        Assert.DoesNotContain("Private", f.Text("SELECT group_concat(data_json) FROM engine_event")!);
        Assert.Equal("{\"optionId\":\"Private A\"}", Field(f, "answer_json"));
    }

    private static OpenNeed ProviderCard(OpenNeed open)
    {
        var card = new AskPayload(open.Kind, "open", null, open.Text, null,
            open.Options.Select(id => new AskOption(id, id, "allow_once")).ToArray(), null, null);
        return open with { ProviderQuestion = JsonSerializer.SerializeToElement(card, RowJsonContext.Rows.AskPayload) };
    }

    private static CommandResult OpenProvider(EngineFixture f, OpenNeed open, string id)
    {
        open = ProviderCard(open);
        var card = JsonSerializer.Deserialize(open.ProviderQuestion!.Value, RowJsonContext.Rows.AskPayload)!;
        var prepared = EngineCommands.PrepareNeedCommand(open);
        CommandResult result = null!;
        RowStore.Open(f.Db).Commit(open.SessionId, new RowConverter().Apply(new AskChanged(1000, open.ChatRowId!, card)), scope =>
        {
            result = new EngineCommands(f.Machine, f.Limits).ApplyPreparedNeed(scope,
                new(Guid.NewGuid(), f.Machine, Adapter, id, 0, 0, 1000), prepared);
        });
        return result;
    }

    private static void SeedLegacy(EngineFixture f, OpenNeed open, string id)
    {
        // Explicit persisted legacy identity; the ordinary question establishes its existing derived state.
        f.Db.Writer.Write("test.persisted-legacy-provider-need", scope =>
        {
            Assert.True(new EngineCommands(f.Machine, f.Limits).Apply(scope,
                new(Guid.NewGuid(), f.Machine, Adapter, id, 0, 0, 1000),
                open with { ProviderRequestId = null, ChatRowId = null }).Applied);
            return scope.Execute("UPDATE engine_needs_me SET provider_request_id=$request,chat_row_id=$chat WHERE id=$id",
                new SqlArg("$request", open.ProviderRequestId), new("$chat", open.ChatRowId), new("$id", id));
        });
    }

    private static CommandResult AnswerOwner(EngineFixture f, string needId, JsonElement original, Guid? commandId = null)
    {
        var identity = f.Db.QueryFirst("""
            SELECT session_id,chat_row_id,provider_request_id,run_id,step_id FROM engine_needs_me WHERE id=$id
            """, r => new
        {
            Session = r.Text(0),
            Chat = r.Text(1),
            Request = r.Text(2),
            Run = r.IsNull(3) ? null : r.Text(3),
            Step = r.IsNull(4) ? null : r.Text(4)
        }, default, new SqlArg("$id", needId))!;
        var attempt = identity.Step is null ? null : f.Db.QueryFirst("SELECT attempt FROM engine_step WHERE id=$id",
            r => (int?)checked((int)r.Long(0)), default, new SqlArg("$id", identity.Step));
        var normalized = JsonSerializer.SerializeToElement(new { optionId = original.GetString() });
        var captured = new PreparedProviderAnswer(new(identity.Session, identity.Chat, identity.Request,
            new(identity.Session, identity.Run, identity.Step, attempt, 0), "acp"), normalized);
        var prepared = EngineCommands.PrepareOwnerNeedCommand(commandId ?? Guid.NewGuid(), f.Machine, needId, 1, 0,
            new ResolveNeed("answered", original), captured, null, default);
        return f.Db.Writer.Write("test.owner-answer", scope => new EngineCommands(f.Machine, f.Limits)
            .ApplyPreparedOwnerNeed(scope, prepared, f.Time.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    private static string? Field(EngineFixture f, string column, string id = "n") =>
        f.Text($"SELECT {column} FROM engine_needs_me WHERE id=$id", new SqlArg("$id", id));

    private static CommandResult Expire(EngineFixture f, string id) => f.Db.Writer.Write("test.expire", s =>
        NeedTransitions.Expire(s, new(Guid.NewGuid(), f.Machine, Adapter, id, 1, 0, f.Time.GetUtcNow().ToUnixTimeMilliseconds())));
}
