using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Features.Agents;

public sealed class DeclaredEnvironmentTests
{
    private static AgentDescriptor Descriptor(IReadOnlyDictionary<string, string>? environment) =>
        new("codex", "Codex", "never-execute", [], environment, VendorFamily: "codex");

    [Fact]
    public void Flattening_the_launch_environment_retains_the_explicit_configuration_guard()
    {
        var launcher = new FakeLauncher { Agents = [Descriptor(new Dictionary<string, string> { ["CODEX_HOME"] = "/fixture/home" })] };
        var captured = ConfigTargetIdentity.Capture(launcher, "codex");
        var effective = new Dictionary<string, string>(captured.Environment) { ["INHERITED_ONLY"] = "retained" };
        var flattened = captured.Descriptor with { Env = effective };

        Assert.Same(launcher.Agents[0], ConfigTargetIdentity.RequireSameConfiguration(launcher, flattened, effective));
        Assert.Equal(["CODEX_HOME"], flattened.DeclaredEnv!.Keys);
        Assert.Equal("retained", flattened.Env!["INHERITED_ONLY"]);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("change")]
    [InlineData("remove")]
    public void A_changed_explicit_environment_refuses_the_original_flattened_capture(string change)
    {
        var original = Descriptor(new Dictionary<string, string> { ["CODEX_HOME"] = "/fixture/original" });
        var launcher = new FakeLauncher { Agents = [original] };
        var captured = ConfigTargetIdentity.Capture(launcher, "codex");
        var replacement = new Dictionary<string, string>(original.Env!);
        if (change == "add") replacement["EXPLICIT_NEW"] = "new";
        else if (change == "change") replacement["CODEX_HOME"] = "/fixture/replaced";
        else replacement.Remove("CODEX_HOME");
        launcher.Agents = [original with { Env = replacement }];

        Assert.Throws<CallFailed>(() => ConfigTargetIdentity.RequireSameConfiguration(launcher,
            captured.Descriptor with { Env = captured.Environment }, captured.Environment));
    }

    [Fact]
    public void A_fresh_capture_of_a_record_with_replaced_configuration_uses_its_current_declared_environment()
    {
        var original = Descriptor(new Dictionary<string, string> { ["CODEX_HOME"] = "/fixture/old" });
        var configured = original with { Env = new Dictionary<string, string> { ["CODEX_HOME"] = "/fixture/current" } };
        var launcher = new FakeLauncher { Agents = [configured] };

        var captured = ConfigTargetIdentity.Capture(launcher, "codex");

        Assert.Equal("/fixture/current", captured.Descriptor.DeclaredEnv!["CODEX_HOME"]);
        Assert.Same(configured, ConfigTargetIdentity.RequireSameConfiguration(launcher,
            captured.Descriptor with { Env = captured.Environment }, captured.Environment));
    }

    [Fact]
    public void Constructor_and_capture_snapshots_are_defensive_and_original_map_removal_is_refused()
    {
        var environment = new Dictionary<string, string> { ["CODEX_HOME"] = "/fixture/original" };
        var configured = Descriptor(environment);
        var launcher = new FakeLauncher { Agents = [configured] };
        var captured = ConfigTargetIdentity.Capture(launcher, "codex");
        var exposed = Assert.IsAssignableFrom<IDictionary<string, string>>(captured.Descriptor.DeclaredEnv);
        Assert.True(exposed.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => exposed.Remove("CODEX_HOME"));
        environment.Remove("CODEX_HOME");

        Assert.Equal("/fixture/original", configured.DeclaredEnv!["CODEX_HOME"]);
        Assert.Equal("/fixture/original", captured.Descriptor.DeclaredEnv!["CODEX_HOME"]);
        Assert.Throws<CallFailed>(() => ConfigTargetIdentity.RequireSameConfiguration(launcher,
            captured.Descriptor with { Env = captured.Environment }, captured.Environment));
    }

    [Fact]
    public void Null_and_empty_declared_maps_are_equivalent_without_changing_C3_target_identity()
    {
        var original = Descriptor(null);
        var launcher = new FakeLauncher { Agents = [original] };
        var captured = ConfigTargetIdentity.Capture(launcher, "codex");
        launcher.Agents = [original with { Env = new Dictionary<string, string>() }];

        Assert.Same(launcher.Agents[0], ConfigTargetIdentity.RequireSameConfiguration(launcher,
            captured.Descriptor with { Env = captured.Environment }, captured.Environment));
        Assert.Equal(captured.TargetId, ConfigTargetIdentity.Of(original with { Cli = "/fixture/another-cli" }, captured.Environment));
    }
}
