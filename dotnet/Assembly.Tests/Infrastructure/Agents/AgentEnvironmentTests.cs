using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Application.Common.Acp;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class AgentEnvironmentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Captured_environment_replaces_ambient_only_for_an_explicit_configured_launch(bool replace)
    {
        var environment = new Dictionary<string, string> { ["ASSEMBLY_CAPTURED_TEST"] = "exact-value" };
        await using var process = AgentProcess.Start(new("/usr/bin/env", [], Path.GetTempPath(), environment,
            ReplaceEnvironment: replace), TranscriptLog.Discard());
        using var output = new StreamReader(process.Output);
        var lines = (await output.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5))).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await process.Exited.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("ASSEMBLY_CAPTURED_TEST=exact-value", lines);
        if (replace) Assert.Equal(["ASSEMBLY_CAPTURED_TEST=exact-value"], lines);
        else Assert.Contains(lines, line => line.StartsWith("PATH=", StringComparison.Ordinal));
    }
}
