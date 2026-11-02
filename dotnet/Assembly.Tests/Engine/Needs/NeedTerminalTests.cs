using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;

namespace Assembly.Tests.Engine.Needs;

public sealed class NeedTerminalTests
{
    [Theory]
    [InlineData(null, "dismissed")]
    [InlineData("", "dismissed")]
    [InlineData(" \t", "dismissed")]
    [InlineData(null, "expired")]
    [InlineData("", "expired")]
    [InlineData(" \t", "expired")]
    [InlineData(null, "cancelled")]
    [InlineData("", "cancelled")]
    [InlineData(" \t", "cancelled")]
    public void Legacy_missing_chat_terminal_has_SQL_null_answer_one_reconciliation_only_effect_and_durable_replay(string? chat, string state)
    {
        using var f = new EngineFixture(); f.SeedSession(state: "needs_me"); SeedLegacy(f, chat);
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(state == "dismissed" ? ActorKind.Window : ActorKind.System, "owner"), "legacy", 1, 0, 1000);
        var command = new ResolveNeed(state, null, state == "cancelled" ? "cancelled" : null);
        var result = f.Apply(context, command);
        Assert.True(result.Applied, result.Error); Assert.Equal(state, f.State("engine_needs_me", "legacy"));
        Assert.Null(f.Text("SELECT answer_json FROM engine_needs_me WHERE id='legacy'"));
        var payload = f.Text("SELECT payload_json FROM engine_outbox WHERE id='answer/legacy'"); Assert.NotNull(payload);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("answer").ValueKind);
        Assert.False(document.RootElement.TryGetProperty("binding", out _)); Assert.False(document.RootElement.TryGetProperty("cancellation", out _));
        Assert.Equal(result, f.Apply(context with { NowMs = 5000 }, command));
        Assert.Equal("already_resolved", f.Apply(context with { CommandId = Guid.NewGuid(), ExpectedVersion = 2 }, command).Error);
        Assert.Equal(1, f.Count("engine_outbox")); Assert.Null(NeedTransitions.NextDeadline(f.Db));
    }

    [Theory]
    [InlineData("permission", "dismissed")]
    [InlineData("permission", "expired")]
    [InlineData("permission", "cancelled")]
    [InlineData("question", "dismissed")]
    [InlineData("question", "expired")]
    [InlineData("question", "cancelled")]
    public void Provider_terminal_uses_exact_current_durable_observation_and_JSON_null_not_deny(string kind, string state)
    {
        using var f = new EngineFixture(); f.SeedSession(state: "needs_me"); SeedLegacy(f, "original-chat", kind);
        var result = f.Apply(new ResolveNeed(state, null, state == "cancelled" ? "cancelled" : null), "legacy", 1,
            actor: new(state == "dismissed" ? ActorKind.Window : ActorKind.System, "owner"));
        Assert.True(result.Applied, result.Error); Assert.Null(f.Text("SELECT answer_json FROM engine_needs_me"));
        using var payload = JsonDocument.Parse(f.Text("SELECT payload_json FROM engine_outbox WHERE id='answer/legacy'")!);
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("answer").ValueKind);
        var binding = payload.RootElement.GetProperty("cancellation");
        Assert.Equal("s", binding.GetProperty("sessionId").GetString()); Assert.Equal("original-chat", binding.GetProperty("chatRowId").GetString());
        Assert.Equal(0, binding.GetProperty("epoch").GetInt64()); Assert.Equal(state, binding.GetProperty("reason").GetString());
        Assert.Equal(1, f.Count("engine_outbox"));
    }

    [Theory]
    [InlineData(null, "provider_gone")]
    [InlineData(" \t", "turn_cancelled")]
    [InlineData("original-chat", "session_ended")]
    public void Already_settled_System_facts_suppress_effects_before_legacy_or_bound_terminal_production(string? chat, string reason)
    {
        using var f = new EngineFixture(); f.SeedSession(state: "needs_me"); SeedLegacy(f, chat);
        Assert.True(f.Apply(new ResolveNeed("cancelled", null, reason), "legacy", 1, actor: new(ActorKind.System, "adapter")).Applied);
        Assert.Null(f.Text("SELECT answer_json FROM engine_needs_me")); Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Fact]
    public void Fresh_raw_provider_answer_requires_preparation_and_its_refusal_receipt_replays()
    {
        using var f = new EngineFixture(); f.SeedSession(state: "needs_me"); SeedLegacy(f, "original-chat");
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "owner"), "legacy", 1, 0, 1000);
        var command = new ResolveNeed("answered", JsonSerializer.SerializeToElement(new { optionId = "once" }));
        var result = f.Apply(context, command);
        Assert.Equal("answer_preparation_required", result.Error); Assert.Equal("open", f.State("engine_needs_me", "legacy"));
        Assert.Equal(0, f.Count("engine_outbox")); Assert.Equal(result, f.Apply(context with { NowMs = 9999 }, command));
    }

    [Fact]
    public void Fresh_unbound_provider_open_is_refused_while_ordinary_nonprovider_scalar_answer_stays_accepted()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var bad = f.Apply(new OpenNeed("s", null, null, "permission", "Run?", ["once"], "request", null, null), "bad",
            actor: new(ActorKind.System, "adapter"));
        Assert.Equal("bad_args", bad.Error); Assert.Equal(0, f.Count("engine_needs_me"));
        Assert.True(f.Apply(new OpenNeed("s", null, null, "question", "Question", [], null, null, null), "plain",
            actor: new(ActorKind.System, "adapter")).Applied);
        Assert.True(f.Apply(new ResolveNeed("answered", JsonSerializer.SerializeToElement(7)), "plain", 1).Applied);
        Assert.Equal("7", f.Text("SELECT answer_json FROM engine_needs_me WHERE id='plain'"));
    }

    private static void SeedLegacy(EngineFixture f, string? chat, string kind = "permission") =>
        f.Db.Writer.Write("test.persisted-legacy-need", s => s.Execute("""
            INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,kind,text,options_json,
                provider_request_id,chat_row_id,state,deadline_ms)
            VALUES('legacy',1,$machine,1000,1000,'s',$kind,'Original','["once"]','connection:7',$chat,'open',1000)
            """, new SqlArg("$machine", f.Machine), new("$kind", kind), new("$chat", chat)));
}
