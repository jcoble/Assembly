using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Tests.Features.Chat.Sessions;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Features.Updates;

public sealed class CliConfigurationTests
{
    [Fact]
    public void Local_cli_guard_rejects_removal_of_an_explicit_captured_environment_entry()
    {
        var launcher = new FakeLauncher
        {
            Agents = [new("selected", "Selected", "never-execute", [],
            new Dictionary<string, string> { ["EXPLICIT_SELECTED_KEY"] = "private-value" }, Cli: "codex", VendorFamily: "codex")]
        };
        var capture = ConfigTargetIdentity.Capture(launcher, "selected");
        launcher.Agents = [launcher.Agents[0] with { Env = null }];
        Assert.Throws<CallFailed>(() => CliCommand.RequireSelectedConfiguration(launcher, capture.Descriptor, capture.Environment));
    }
}
