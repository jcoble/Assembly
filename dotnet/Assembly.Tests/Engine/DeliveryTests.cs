using System.Text.Json;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Assembly.Application.Common.Database;
namespace Assembly.Tests.Engine;

public sealed class DeliveryTests
{
    private static readonly EngineActor System = new(ActorKind.System, "adapter");
    private static void Tree(EngineFixture f)
    {
        f.SeedSession("parent");
        foreach (var id in new[] { "child", "sib-a", "sib-b" })
            f.SeedSession(id, parentId: "parent");
        f.SeedSession("other-top");
    }
    [Theory]
    [InlineData("parent", "child", "steer")]
    [InlineData("child", "parent", "steer")]
    [InlineData("sib-a", "sib-b", "steer")]
    [InlineData(null, "child", "steer")]
    [InlineData("other-top", "child", "wait")]
    [InlineData("other-top", "parent", "wait")]
    public void Rule_is_decided_once_and_stored(string? from, string to, string expected)
    {
        using var f = new EngineFixture();
        Tree(f);
        Assert.True(f.Apply(new SendMessage(from, to, "hi", "prompt"), "m", actor: from == null ? null : new(ActorKind.Session, from)).Applied);
        Assert.Equal(expected, f.Text("SELECT delivery FROM engine_message"));
    }
    [Fact]
    public void Idle_recipient_gets_one_prompt_effect()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        f.Apply(new SendMessage(null, "s", "hi", "prompt"), "m");
        Assert.Equal("send/m/1", f.Text("SELECT id FROM engine_outbox"));
        Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message"));
        Assert.Equal("prompt", JsonDocument.Parse(f.Text("SELECT payload_json FROM engine_outbox")!).RootElement.GetProperty("how").GetString());
    }
    [Fact]
    public void Second_message_waits_until_first_has_an_outcome()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        f.Apply(new SendMessage(null, "s", "one", "prompt"), "m1");
        f.Apply(new SendMessage(null, "s", "two", "prompt"), "m2");
        Assert.Equal(1, f.Count("engine_outbox"));
        Assert.True(f.Apply(new MessageOutcome("next_turn", "turn", null), "m1", 1, actor: System).Applied);
        Assert.Equal("running", f.State("engine_session", "s"));
        Assert.Equal(1, f.Count("engine_outbox"));
        State(f, "idle");
        Assert.Equal(2, f.Count("engine_outbox"));
    }
    [Fact]
    public void Busy_outcome_sends_nothing_until_the_next_state_change()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        f.Apply(new SendMessage(null, "s", "hi", "prompt"), "m");
        Assert.True(f.Apply(new MessageOutcome("queued", null, "busy"), "m", 1, actor: System).Applied);
        Assert.Equal(0, f.Number("SELECT in_flight FROM engine_message"));
        Assert.Equal(1, f.Count("engine_outbox"));
        State(f, "running");
        State(f, "idle");
        Assert.Equal("send/m/2", f.Text("SELECT id FROM engine_outbox ORDER BY rowid DESC LIMIT 1"));
    }
    [Fact]
    public void Queued_after_a_state_change_that_landed_first_releases_again()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        f.Apply(new SendMessage(null, "s", "hi", "prompt"), "m");
        State(f, "running");
        State(f, "idle");
        Assert.True(f.Apply(new MessageOutcome("queued", null, "busy"), "m", 1, actor: System).Applied);
        Assert.Equal(2, f.Number("SELECT attempt FROM engine_message"));
        Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message"));
    }
    [Fact]
    public void Twenty_first_message_is_refused_but_window_is_uncapped()
    {
        using var f = new EngineFixture();
        f.SeedSession("a");
        f.SeedSession();
        for (int i = 0; i < 20; i++)
            Assert.True(f.Apply(new SendMessage("a", "s", "hi", "prompt"), "m" + i, actor: new(ActorKind.Session, "a")).Applied);
        Assert.Equal("message_limit", f.Apply(new SendMessage("a", "s", "hi", "prompt"), "over", actor: new(ActorKind.Session, "a")).Error);
        for (int i = 0; i < 25; i++)
            Assert.True(f.Apply(new SendMessage(null, "s", "hi", "prompt"), "w" + i).Applied);
    }
    [Fact]
    public void Child_ended_note_uses_the_rule_and_release()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        f.SeedSession("child/x", parentId: "s");
        Assert.True(f.Apply(new SetSessionState("ended", null, null), "child/x", actor: System).Applied);
        Assert.Equal("send/child-ended/child/x/1", f.Text("SELECT id FROM engine_outbox"));
        Assert.Equal("steer", f.Text("SELECT delivery FROM engine_message"));
    }
    [Fact]
    public void Unsupported_outcome_retains_approved_terminal_semantics()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        f.Apply(new SendMessage(null, "s", "hi", "prompt"), "m");
        Assert.True(f.Apply(new MessageOutcome("unsupported", null, "historical contract"), "m", 1, actor: System).Applied);
        Assert.Equal(0, f.Number("SELECT in_flight FROM engine_message"));
    }
    [Fact]
    public void Terminal_messages_still_count_toward_pair_limit()
    {
        using var f = new EngineFixture();
        f.SeedSession("a");
        f.SeedSession();
        Assert.True(f.Apply(new SendMessage("a", "s", "first", "prompt"), "m0", actor: new(ActorKind.Session, "a")).Applied);
        Assert.True(f.Apply(new MessageOutcome("failed", null, "rejected"), "m0", 1, actor: System).Applied);
        for (int i = 1; i < 20; i++)
            Assert.True(f.Apply(new SendMessage("a", "s", "hi", "prompt"), "m" + i, actor: new(ActorKind.Session, "a")).Applied);
        Assert.Equal("message_limit", f.Apply(new SendMessage("a", "s", "hi", "prompt"), "over", actor: new(ActorKind.Session, "a")).Error);
    }
    [Fact]
    public void Permanent_send_failure_clears_reservation_and_releases_next_message()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        f.Apply(new SendMessage(null, "s", "one", "prompt"), "m1");
        f.Apply(new SendMessage(null, "s", "two", "prompt"), "m2");
        Assert.True(f.Apply(new Assembly.Engine.Effects.ClaimEffect(), "send/m1/1", actor: System).Applied);
        var receipt = new Assembly.Engine.Effects.EffectReceipt(Assembly.Engine.Effects.EffectDisposition.Failed, JsonSerializer.SerializeToElement((string?)null), "rejected");
        Assert.True(f.Apply(new Assembly.Engine.Effects.CompleteEffect(receipt), "send/m1/1", 1, actor: System).Applied);
        Assert.Equal("failed", f.State("engine_message", "m1"));
        Assert.Equal(0, f.Number("SELECT in_flight FROM engine_message WHERE id='m1'"));
        Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message WHERE id='m2'"));
    }
    [Fact]
    public void Configured_message_limit_is_read_for_each_send()
    {
        using var f = new EngineFixture();
        f.SeedSession("a");
        f.SeedSession();
        int limit = 1;
        var commands = new Assembly.Engine.EngineCommands(f.Machine, () => 5, () => limit);
        Assembly.Engine.Contracts.CommandResult Send(string id) => f.Db.Writer.Write("test.limit", scope => commands.Apply(scope, new(Guid.NewGuid(), f.Machine, new(ActorKind.Session, "a"), id, 0, 0, 1000), new SendMessage("a", "s", "hi", "prompt")));
        Assert.True(Send("one").Applied);
        Assert.Equal("message_limit", Send("two").Error);
        limit = 2;
        Assert.True(Send("three").Applied);
    }
    [Fact]
    public void Child_ended_release_publishes_the_older_selected_message_once()
    {
        using var f = new EngineFixture(); f.SeedSession(); f.SeedSession("child", parentId: "s");
        Assert.True(f.Apply(new SendMessage(null, "s", "older", "prompt"), "older").Applied);
        Assert.True(f.Apply(new MessageOutcome("queued", null, "busy"), "older", 1, actor: System).Applied);
        var before = f.Events().Last().Seq;
        Assert.True(f.Apply(new SetSessionState("ended", null, null), "child", actor: System).Applied);
        var committed = f.Events().Where(e => e.Seq > before).ToArray();
        Assert.Single(committed, e => e.EntityKind == "message" && e.EntityId == "older" && e.Data.State == "queued");
        Assert.Single(committed, e => e.EntityKind == "message" && e.EntityId == "child-ended/child");
        Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message WHERE id='older'"));
        Assert.Equal(0, f.Number("SELECT in_flight FROM engine_message WHERE id='child-ended/child'"));
        Assert.Equal(committed.Max(e => e.Seq), f.Number("SELECT event_seq FROM engine_outbox WHERE id='send/older/2'"));
    }

    private static void State(EngineFixture f, string state) => Assert.True(f.Apply(new SetSessionState(state, null, null), "s", f.Number("SELECT version FROM engine_session WHERE id='s'"), actor: System).Applied);
}
