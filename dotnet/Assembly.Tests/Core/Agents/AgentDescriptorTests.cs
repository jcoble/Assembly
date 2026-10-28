using Assembly.Application.Common.Agents;

namespace Assembly.Tests.Core.Agents;

/// <summary>The agents file: each agent may name its CLI's full path, for a backend started with a short PATH.</summary>
public sealed class AgentDescriptorTests
{
    [Fact]
    public void An_agent_s_cli_path_is_read_and_used()
    {
        var agents = AgentDescriptor.Parse("""[{"id":"claude","name":"Claude Code","command":"node","args":[],"cli":"/home/me/.local/bin/claude"}]""");

        Assert.Equal("/home/me/.local/bin/claude", agents[0].Cli);
        Assert.Equal("/home/me/.local/bin/claude", AgentDescriptor.CliOf(agents, "claude"));
    }

    [Fact]
    public void An_agents_file_without_cli_paths_still_loads_and_uses_the_plain_names()
    {
        var agents = AgentDescriptor.Parse("""[{"id":"codex","name":"Codex","command":"node","args":[]}]""");

        Assert.Null(agents[0].Cli);
        Assert.Equal("codex", AgentDescriptor.CliOf(agents, "codex"));
        Assert.Equal("claude", AgentDescriptor.CliOf(agents, "claude"));
    }
}
