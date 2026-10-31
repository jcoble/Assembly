using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine;
using Assembly.Application.Features.Engine.NeedsMe;
using Assembly.Application.Features.Engine.Sessions;
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
        Assert.True(f.Apply(new OpenNeed("s", null, null, "permission", "Allow?", [],
            "request-7", "chat-row-2", 2000), "n", actor: Adapter).Applied);
        var first = f.Apply(new ResolveNeed("answered", Allow), "n", version: 1);
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
    public void Timeout_permission_denies_question_waits()
    {
        using var f = RunningStep();
        Assert.True(f.Apply(new OpenNeed("s", "r", "step", "question", "Secret question", [], null, null, 2000), "q", actor: Adapter).Applied);
        Assert.True(f.Apply(new OpenNeed("s", "r", null, "permission", "Allow?", [], "request-7", null, 2000), "n", actor: Adapter).Applied);
        Assert.Equal(2000, NeedTransitions.NextDeadline(f.Db));
        f.Time.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(1999));
        Assert.False(Expire(f, "n").Applied); Assert.False(Expire(f, "q").Applied);
        Assert.Equal("open", f.State("engine_needs_me", "n")); Assert.Equal(0, f.Count("engine_outbox"));
        f.Time.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(2000));
        Assert.True(Expire(f, "n").Applied); Assert.True(Expire(f, "q").Applied);
        Assert.Equal("expired", f.State("engine_needs_me", "n")); Assert.Equal("expired", f.State("engine_needs_me", "q"));
        Assert.Equal("\"deny\"", Field(f, "answer_json")); Assert.Null(Field(f, "answer_json", "q"));
        Assert.Equal("waiting", f.State("engine_step", "step"));
        Assert.Equal("waiting", f.Text("SELECT state FROM engine_step_attempt"));
        Assert.Equal(1, f.Count("engine_outbox")); Assert.Null(NeedTransitions.NextDeadline(f.Db));
        using var payload = JsonDocument.Parse(f.Text("SELECT payload_json FROM engine_outbox")!);
        Assert.Equal("request-7", payload.RootElement.GetProperty("providerRequestId").GetString());
        Assert.Equal("deny", payload.RootElement.GetProperty("answer").GetString());
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
        var open = new OpenNeed("s", null, null, "permission", "Allow?", [], "request-7", "chat-row-2", null);
        Assert.True(f.Apply(open, "n", actor: Adapter).Applied);
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
    [InlineData("answered", ActorKind.System, false)]
    [InlineData("dismissed", ActorKind.System, false)]
    [InlineData("expired", ActorKind.Window, false)]
    [InlineData("cancelled", ActorKind.Window, false)]
    [InlineData("answered", ActorKind.Session, false)]
    public void Resolution_edges_record_one_actor_and_time(string state, ActorKind actor, bool allowed)
    {
        using var f = new EngineFixture(); f.SeedSession();
        f.Apply(new OpenNeed("s", null, null, "permission", "Allow?", [], "request", null, 2000), "n", actor: Adapter);
        f.Time.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(2000));
        var result = f.Apply(new ResolveNeed(state, state == "answered" ? Allow : null), "n", version: 1, actor: new(actor, "resolver"));
        Assert.Equal(allowed, result.Applied);
        if (!allowed) { Assert.Equal("forbidden", result.Error); Assert.Null(Field(f, "answered_by")); return; }
        Assert.Equal("resolver", Field(f, "answered_by")); Assert.Equal(2000, f.Number("SELECT answered_ms FROM engine_needs_me"));
        Assert.Equal(state == "answered" ? "\"allow\"" : "\"deny\"", Field(f, "answer_json"));
        Assert.Equal("already_resolved", f.Apply(new ResolveNeed("answered", Allow), "n", version: 2).Error);
        Assert.Equal("resolver", Field(f, "answered_by")); Assert.Equal(1, f.Count("engine_outbox"));
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
        var open = new OpenNeed("s", "r", "step", "question", "Question", [], "request", null, null);
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
        Assert.True(f.Apply(open, "n", actor: new(ActorKind.Session, "s")).Applied);
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
        Assert.True(f.Apply(new OpenNeed("s", null, null, kind, "Private text", ["Private A", "Private B"], "request", null, 1000), "n", actor: Adapter).Applied);
        Assert.True(Expire(f, "n").Applied);
        Assert.Null(Field(f, "answer_json")); Assert.Equal(0, f.Count("engine_outbox"));
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
        Assert.True(f.Apply(new OpenNeed("s", "r", "step", "permission", "Private question", ["Private option"], "request", null, null), "n", actor: Adapter).Applied);
        var events = f.Count("engine_event"); var receipts = f.Count("engine_command_receipt");
        var next = f.Number("SELECT next_seq FROM engine_meta");
        f.Db.Writer.Write("test.fail-answer", s => s.Execute("CREATE TRIGGER fail_answer AFTER INSERT ON engine_outbox BEGIN SELECT RAISE(ABORT,'injected'); END"));
        Assert.Throws<SqliteException>(() => f.Apply(new ResolveNeed("answered", Allow), "n", version: 1));
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
        Assert.True(f.Apply(new OpenNeed("s", null, null, "choice", "Private question", ["Private A", "Private B"], "request", "chat", null), "n", actor: Adapter).Applied);
        var answer = JsonSerializer.SerializeToElement("Private A");
        var id = Guid.NewGuid();
        var result = f.Apply(new ResolveNeed("answered", answer), "n", version: 1, commandId: id);
        Assert.True(result.Applied);
        f.Reopen();
        Assert.Equal(result, f.Apply(new ResolveNeed("answered", answer), "n", version: 1, commandId: id));
        Assert.Equal(1, f.Count("engine_outbox"));
        Assert.DoesNotContain("Private", f.Text("SELECT group_concat(data_json) FROM engine_event")!);
        Assert.Equal("\"Private A\"", Field(f, "answer_json"));
    }

    private static string? Field(EngineFixture f, string column, string id = "n") =>
        f.Text($"SELECT {column} FROM engine_needs_me WHERE id=$id", new SqlArg("$id", id));

    private static CommandResult Expire(EngineFixture f, string id) => f.Db.Writer.Write("test.expire", s =>
        NeedTransitions.Expire(s, new(Guid.NewGuid(), f.Machine, Adapter, id, 1, 0, f.Time.GetUtcNow().ToUnixTimeMilliseconds())));
}
