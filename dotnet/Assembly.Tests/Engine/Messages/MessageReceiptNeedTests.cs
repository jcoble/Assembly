using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.NeedsMe;

namespace Assembly.Tests.Engine.Messages;

/// <summary>A send receipt that lands after an approval opened, as when an agent asks in the same instant it starts.</summary>
public sealed class MessageReceiptNeedTests
{
    private static readonly EngineActor Adapter = new(ActorKind.System, "adapter");

    [Theory]
    [InlineData(true, "needs_me")]
    [InlineData(false, "running")]
    public void A_next_turn_receipt_keeps_an_open_need_showing(bool needOpen, string state)
    {
        using var f = new EngineFixture(); f.SeedSession("b");
        Assert.True(f.Apply(new SendMessage(null, "b", "hello", "prompt"), "m").Applied);
        if (needOpen)
        {
            var opened = f.Apply(new OpenNeed("b", null, null, "question", "Which file?", [], null, null, null), "n", actor: Adapter);
            Assert.True(opened.Applied, opened.Error);
        }

        Assert.True(f.Apply(new MessageOutcome("next_turn", "turn-1", null), "m", version: 2, actor: Adapter).Applied);

        Assert.Equal(state, f.State("engine_session", "b"));
        Assert.Equal("running", f.Text("SELECT activity_state FROM engine_session WHERE id='b'"));
        Assert.Equal(state, f.Events().Last(e => e.Kind == "session.changed" && e.EntityId == "b").Data.State);
    }
}
