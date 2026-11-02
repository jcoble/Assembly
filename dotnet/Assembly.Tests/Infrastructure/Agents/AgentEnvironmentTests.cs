using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Acp;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Transcript;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class AgentEnvironmentTests
{
    [Theory]
    [InlineData(false, false, "inherited")]
    [InlineData(true, false, "absent")]
    [InlineData(true, true, "overridden")]
    public async Task Actual_child_uses_captured_environment_with_legacy_omission_control(bool replace, bool overridden, string expected)
    {
        // Only presence/equality is returned. No inherited environment contents enter output or proof.
        var environment = new Dictionary<string, string>();
        if (overridden) environment["PATH"] = "a39-fixture";
        var program = "print !exists($ENV{PATH}) ? qq(absent\\n) : $ENV{PATH} eq q(a39-fixture) ? qq(overridden\\n) : qq(inherited\\n);";
        await using var child = AgentProcess.Start(new AgentCommand("/usr/bin/perl", ["-e", program], "/tmp", environment, replace), TranscriptLog.Discard());
        using var output = new StreamReader(child.Output, leaveOpen: true);
        Assert.Equal(expected, await output.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await child.Exited.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
