using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.Mcp;

public sealed class MessageContactTests
{
    [Theory]
    [InlineData("parent", "child", "steer")]
    [InlineData("child", "parent", "steer")]
    [InlineData("child", "sibling", "steer")]
    [InlineData("child", "other", "wait")]
    public void Local_contact_preserves_independent_delivery_rule(string from, string to, string delivery)
    {
        using var f = Tree();
        Assert.True(f.Apply(new SendMessage(from, to, "body", "prompt"), "m", actor: new(ActorKind.Session, from)).Applied);
        Assert.Equal(delivery, f.Text("SELECT delivery FROM engine_message"));
    }
    [Theory]
    [InlineData("child", "cousin")]
    [InlineData("other", "child")]
    [InlineData("child", "child")]
    public void Unrelated_nonroot_and_self_are_not_contactable(string from, string to)
    {
        using var f = Tree();
        Assert.Equal("not_reachable", f.Apply(new SendMessage(from, to, "body", "prompt"), "m", actor: new(ActorKind.Session, from)).Error);
        Assert.Equal(0, f.Count("engine_message")); Assert.Equal(0, f.Count("engine_outbox"));
    }
    [Fact]
    public void Contact_guard_also_applies_to_the_existing_prepared_producer()
    {
        using var f = Tree();
        var p = PreparedHelperPrompt.Prepare(new SendMessage("other", "child", "[message from quote]\0body", "prompt"), "Test", 0);
        var result = f.Db.Writer.Write("prepared", s => new Assembly.Engine.EngineCommands(f.Machine, f.Limits).ApplyPreparedHelperPrompt(s,
            new(Guid.NewGuid(), f.Machine, new(ActorKind.Session, "other"), "m", 0, 0, 1000), p));
        Assert.Equal("not_reachable", result.Error); Assert.Equal(0, f.Count("engine_message"));
    }
    private static EngineFixture Tree()
    {
        var f = new EngineFixture(); f.SeedSession("parent"); f.SeedSession("child", parentId: "parent");
        f.SeedSession("sibling", parentId: "parent"); f.SeedSession("cousin", parentId: "sibling"); f.SeedSession("other"); return f;
    }
}
