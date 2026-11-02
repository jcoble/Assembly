using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;

namespace Assembly.Tests.Engine.Mcp;

public sealed partial class StartSessionToolTests
{
    [Fact]
    public async Task Machine_null_keeps_old_generated_bytes_and_the_local_atomic_path()
    {
        var args = new StartSessionArguments("Task", "Helper");
        Assert.Equal("""{"prompt":"Task","title":"Helper","provider":null,"model":null,"folder":null,"taskUrl":null}""",
            System.Text.Json.JsonSerializer.Serialize(args, McpJson.Default.StartSessionArguments));
        await using var h = new Harness(); h.Fixture.SeedSession("owner");
        var answer = await h.Call(h.Start, "owner", """{"prompt":"Task","title":"Helper","machine":null}""");
        Assert.False(answer.IsError, Text(answer)); Assert.Equal(1, h.Fixture.Count("engine_message"));
        Assert.True((await h.Call(h.Start, "owner", """{"prompt":"Task","title":"Helper","machine":null,"provider":null}""")).IsError);
        Assert.True((await h.Call(h.Start, "owner", """{"prompt":"Task","title":"Helper","machine":" "}""")).IsError);
    }

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

    [Fact]
    public async Task Remote_start_without_coordinator_honestly_directs_the_caller_to_the_Mac()
    {
        await using var h = new Harness(); h.Fixture.SeedSession("owner");
        var answer = await h.Call(h.Start, "owner", """{"prompt":"Task","title":"Helper","machine":"remote","folder":"relative-target-only","provider":"target-only"}""");
        Assert.True(answer.IsError); Assert.Contains("start it from the Mac", Text(answer));
        Assert.Equal(1, h.Fixture.Count("engine_session")); Assert.Equal(0, h.Fixture.Count("engine_message"));
    }

    [Fact]
    public async Task Explicit_current_machine_keeps_the_same_local_atomic_start_path()
    {
        await using var h = new Harness(); h.Fixture.SeedSession("owner");
        var args = "{\"prompt\":\"Task\",\"title\":\"Helper\",\"machine\":" + System.Text.Json.JsonSerializer.Serialize(h.Fixture.Machine) + "}";
        var answer = await h.Call(h.Start, "owner", args);
        Assert.False(answer.IsError, Text(answer));
        Assert.Equal(1, h.Fixture.Count("engine_message"));
        Assert.Equal(1, h.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE parent_id='owner'"));
    }
}
