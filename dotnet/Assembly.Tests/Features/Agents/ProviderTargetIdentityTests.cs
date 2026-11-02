using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Agents;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Tests.Features.Agents;

public sealed class ProviderTargetIdentityTests
{
    [Fact]
    public void Frozen_C3_vector_preserves_identity_only_Air_and_null_empty_equivalence()
    {
        var descriptor = new AgentDescriptor("a", "Name", "cmd", ["ab", "c"], null, Air: ["air"]);
        var env = new Dictionary<string, string> { ["HOME"] = "/same" };
        Assert.Equal("03b684106ca43ac3f1d64dea87693645d614c9e6af67b56b588b938a503d28aa", ConfigTargetIdentity.Of(descriptor, env));
        Assert.NotEqual(ConfigTargetIdentity.Of(descriptor, env), ConfigTargetIdentity.Of(descriptor with { Args = ["a", "bc"] }, env));
        Assert.NotEqual(ConfigTargetIdentity.Of(descriptor, env), ConfigTargetIdentity.Of(descriptor with { Air = ["different"] }, env));
        Assert.Equal(ConfigTargetIdentity.Of(descriptor with { Air = null }, env), ConfigTargetIdentity.Of(descriptor with { Air = [] }, env));
        Assert.Equal(ConfigTargetIdentity.Of(descriptor, env), ConfigTargetIdentity.Of(descriptor with { Name = "Renamed", Cli = "another-cli" }, env));
    }

    [Fact]
    public void Target_environment_order_is_canonical_and_account_context_uses_the_same_map()
    {
        var descriptor = new AgentDescriptor("custom-provider", "Provider", "cmd", [],
            new Dictionary<string, string> { ["CODEX_HOME"] = "/isolated-profile" }, VendorFamily: "codex");
        var environment = ConfigTargetIdentity.EnvironmentOf(descriptor);
        Assert.Equal("/isolated-profile", environment["CODEX_HOME"]);
        Assert.Equal("acct-v1:bba8f0244af078a391d9faaebbd7df99750b21889fda2190393b2863a58c6e5b",
            AccountContext.Of(descriptor, key => environment.GetValueOrDefault(key)));
        var reversed = environment.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(ConfigTargetIdentity.Of(descriptor, environment), ConfigTargetIdentity.Of(descriptor, reversed));
        reversed["CODEX_HOME"] = "/replaced-profile";
        Assert.NotEqual(ConfigTargetIdentity.Of(descriptor, environment), ConfigTargetIdentity.Of(descriptor, reversed));
    }

    [Fact]
    public void Capture_freezes_descriptor_collections_and_refuses_a_replaced_configuration()
    {
        var args = new List<string> { "--original" };
        var env = new Dictionary<string, string> { ["CODEX_HOME"] = "/original" };
        var descriptor = new AgentDescriptor("codex", "Codex", "program", args, env, VendorFamily: "codex");
        var launcher = new AdapterLauncher([descriptor], Path.GetTempPath(), new UnclaimedPermissions(), _ => null);
        var captured = ConfigTargetIdentity.Capture(launcher, "codex");
        args[0] = "--replaced";
        env["CODEX_HOME"] = "/replaced";
        Assert.Equal(["--original"], captured.Descriptor.Args);
        Assert.Equal("/original", captured.Descriptor.Env!["CODEX_HOME"]);
        Assert.Equal("/original", captured.Environment["CODEX_HOME"]);
        Assert.Throws<CallFailed>(() => ConfigTargetIdentity.Capture(launcher, "codex", captured.TargetId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Configured_launch_refuses_current_command_arguments_profile_or_cli_replacement(int replacement)
    {
        var descriptor = new AgentDescriptor("codex", "Codex", "/missing-provider-canary", ["--base"],
            new Dictionary<string, string> { ["CODEX_HOME"] = "/original" }, Cli: "/original-cli", VendorFamily: "codex");
        var agents = new List<AgentDescriptor> { descriptor };
        var launcher = new AdapterLauncher(agents, Path.GetTempPath(), new UnclaimedPermissions(), _ => null);
        var captured = ConfigTargetIdentity.Capture(launcher, "codex");
        agents[0] = replacement switch
        {
            0 => descriptor with { Args = ["--replaced"] },
            1 => descriptor with { Env = new Dictionary<string, string> { ["CODEX_HOME"] = "/replaced" } },
            _ => descriptor with { Cli = "/replaced-cli" }
        };
        await Assert.ThrowsAsync<CallFailed>(() => launcher.LaunchConfiguredAsync(captured.Descriptor,
            captured.Environment, new StageTimes(TimeProvider.System), CancellationToken.None));
    }

    [Fact]
    public void Invalid_descriptor_is_detected_without_starting_a_process()
    {
        var valid = new AgentDescriptor("id", "Name", "program", [], null);
        Assert.Null(valid.Invalid());
        Assert.NotNull((valid with { Id = " " }).Invalid());
        Assert.NotNull((valid with { Name = "" }).Invalid());
        Assert.NotNull((valid with { Command = "\t" }).Invalid());
    }

    [Fact]
    public async Task Same_invalid_configuration_can_be_observed_but_still_cannot_launch()
    {
        var descriptor = new AgentDescriptor("fixture", "Fixture", "", [], null);
        var launcher = new AdapterLauncher([descriptor], Path.GetTempPath(), new UnclaimedPermissions(), _ => null);
        var captured = ConfigTargetIdentity.Capture(launcher, descriptor.Id);
        Assert.Same(descriptor, ConfigTargetIdentity.RequireSameConfiguration(launcher, captured.Descriptor, captured.Environment));
        Assert.Throws<CallFailed>(() => ConfigTargetIdentity.Validate(launcher, captured.Descriptor, captured.Environment));
        await Assert.ThrowsAsync<CallFailed>(() => launcher.LaunchConfiguredAsync(captured.Descriptor, captured.Environment,
            new StageTimes(TimeProvider.System), CancellationToken.None));
    }

    [Theory]
    [InlineData("command")]
    [InlineData("context")]
    [InlineData("cli")]
    [InlineData("vendor")]
    [InlineData("removed")]
    public void Observation_and_launch_share_the_same_configuration_replacement_guard(string replacement)
    {
        var descriptor = new AgentDescriptor("fixture", "Fixture", "program", [],
            new Dictionary<string, string> { ["CODEX_HOME"] = "/fixture" }, VendorFamily: "codex");
        var agents = new List<AgentDescriptor> { descriptor };
        var launcher = new AdapterLauncher(agents, Path.GetTempPath(), new UnclaimedPermissions(), _ => null);
        var captured = ConfigTargetIdentity.Capture(launcher, descriptor.Id);
        if (replacement == "removed") agents.Clear();
        else agents[0] = replacement switch
        {
            "command" => descriptor with { Command = "different" },
            "context" => descriptor with { Env = new Dictionary<string, string> { ["CODEX_HOME"] = "/different" } },
            "cli" => descriptor with { Cli = "/different" },
            _ => descriptor with { VendorFamily = "claude" }
        };
        Assert.Throws<CallFailed>(() => ConfigTargetIdentity.RequireSameConfiguration(launcher, captured.Descriptor, captured.Environment));
        Assert.Throws<CallFailed>(() => ConfigTargetIdentity.Validate(launcher, captured.Descriptor, captured.Environment));
    }

    [Fact]
    public void Models_use_the_existing_model_selector_policy_and_preserve_provider_order()
    {
        using var options = JsonDocument.Parse("""
            [{"id":"effort","category":"thought_level","type":"select","options":[{"value":"high","name":"High"}]},
             {"id":"model","category":"model","type":"select","options":[{"value":"m1","name":"First"},{"value":"m2","name":"Second"}]}]
            """);
        Assert.Equal([("m1", "First"), ("m2", "Second")], ConfigOptions.Models(options.RootElement));
        Assert.Empty(ConfigOptions.Models(default));
    }
}
