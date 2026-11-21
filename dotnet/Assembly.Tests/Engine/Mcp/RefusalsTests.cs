using Assembly.Engine.Mcp;

namespace Assembly.Tests.Engine.Mcp;

public sealed class RefusalsTests
{
    [Fact]
    public void Argument_refusal_names_the_field() => Assert.Equal("start_session: title is required.",
        Refusals.Text("start_session", new("bad_args", new Dictionary<string, string>
        {
            ["field"] = "title",
            ["problem"] = "is required"
        })));
}
