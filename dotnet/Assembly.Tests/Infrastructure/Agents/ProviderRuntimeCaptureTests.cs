using System.Text.Json.Nodes;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Updates;
using Assembly.Tests.Features.Updates;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class ProviderRuntimeCaptureTests
{
    [Fact]
    public async Task Private_capture_roundtrip_reconstructs_the_declared_map_and_refuses_actual_source_home_removal()
    {
        using var fixture = new CliCheckFixture();
        var captured = RemoteCliCaptureTests.Capture(fixture);
        var file = Path.Combine(fixture.Home, "runtime-private-capture");
        await RemoteCliCapture.SaveAsync(file, captured, default);

        var loaded = RemoteCliCapture.Load(file);
        var target = Assert.Single(loaded.Targets);
        var flattened = target.Descriptor with { Env = target.Environment };
        Assert.Same(fixture.Agent, ConfigTargetIdentity.RequireSameConfiguration(fixture.Launcher,
            flattened, target.Environment));
        Assert.Equal(fixture.Agent.Env!["CODEX_HOME"], flattened.DeclaredEnv!["CODEX_HOME"]);
        Assert.DoesNotContain("declaredEnv", File.ReadAllText(file));
        Assert.Single(RemoteCliCapture.ReadCurrentAgents(loaded));

        var source = JsonNode.Parse(File.ReadAllText(captured.AgentsFile))!.AsArray();
        Assert.True(source[0]!["env"]!.AsObject().Remove("CODEX_HOME"));
        File.WriteAllText(captured.AgentsFile, source.ToJsonString());
        fixture.Launcher.Agents = AgentDescriptor.Parse(File.ReadAllText(captured.AgentsFile));

        Assert.Throws<CallFailed>(() => ConfigTargetIdentity.RequireSameConfiguration(fixture.Launcher,
            flattened, target.Environment));
        Assert.Throws<CallFailed>(() => RemoteCliCapture.ReadCurrentAgents(loaded));
        Assert.False(File.Exists(Path.Combine(fixture.Home, "probe-record")));
    }
}
