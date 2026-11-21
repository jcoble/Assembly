using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;

namespace Assembly.Tests.Engine.Mcp;

public sealed partial class StartSessionToolTests
{
    [Fact]
    public async Task Remote_port_receives_authenticated_caller_and_complete_arguments_before_local_validation()
    {
        await using var h = new Harness(); h.Fixture.SeedSession("owner"); var port = new Starts();
        var tool = new StartSessionTool(h.Gate, () => throw new InvalidOperationException("local provider lookup"),
            _ => throw new InvalidOperationException("local folder lookup"), port);
        var answer = await h.Call(tool, "owner", """{"prompt":"Task\nSUFFIX😀","title":"Helper","machine":"remote","folder":"/target-only","provider":"target-only"}""");
        Assert.False(answer.IsError, Text(answer)); Assert.Equal(new McpCaller("owner", 0), port.Caller);
        Assert.Equal("remote", port.Arguments!.Machine); Assert.Equal("/target-only", port.Arguments.Folder);
        Assert.Equal("target-only", port.Arguments.Provider); Assert.Equal("Task\nSUFFIX😀", port.Arguments.Prompt);
        Assert.Equal(0, h.Fixture.Count("engine_message")); Assert.Equal(1, h.Fixture.Count("engine_session"));
    }

    private sealed class Starts : IMcpMachineStarts
    {
        internal McpCaller? Caller; internal StartSessionArguments? Arguments;
        public Task<StartSessionAnswer> StartAsync(McpCaller caller, StartSessionArguments arguments, CancellationToken cancellationToken)
        { Caller = caller; Arguments = arguments; return Task.FromResult(new StartSessionAnswer("child", "message")); }
    }
}
