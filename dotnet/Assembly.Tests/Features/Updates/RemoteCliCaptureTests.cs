using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Features.Updates;

public sealed class RemoteCliCaptureTests
{
    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public async Task Owning_custom_source_and_private_account_are_used_without_adapter_launch(string family)
    {
        using var f = new CliCheckFixture(family);
        var capture = Capture(f);
        var result = await new RemoteCliCheck(f.Time).ReadAsync(capture, f.Args, default);
        Assert.Equal(f.Version, result.InstalledVersion);
        Assert.Equal("npm", result.Manager); Assert.Equal("npm", result.Source);
        Assert.Null(result.Reason);
        Assert.Equal(capture.InstallationId, result.InstallationId);
        var safe = JsonSerializer.Serialize(result, RemoteCliJson.Default.RemoteCliReadResult);
        Assert.DoesNotContain(f.Home, safe); Assert.DoesNotContain("PRIVATE_ARG", safe);
        Assert.Contains(Path.Combine(f.Home, "private-account-canary"), File.ReadAllText(Path.Combine(f.Home, "probe-record")));
    }

    [Theory]
    [InlineData("cli")]
    [InlineData("source")]
    [InlineData("account")]
    [InlineData("target")]
    [InlineData("machine")]
    public async Task Replacement_or_wrong_selection_never_executes_a_cli(string change)
    {
        using var f = new CliCheckFixture();
        var capture = Capture(f);
        var args = f.Args;
        if (change == "cli") File.WriteAllText(capture.AgentsFile, JsonSerializer.Serialize(new[] { f.Agent with { Cli = "/different/cli" } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        if (change == "source") File.AppendAllText(capture.AgentsFile, " ");
        if (change == "account") args = args with { AccountContext = "acct-v1:" + new string('0', 64) };
        if (change == "target") args = args with { ConfigurationTargetId = new string('0', 64) };
        if (change == "machine") args = args with { MachineId = "other" };
        await Assert.ThrowsAsync<CallFailed>(() => new RemoteCliCheck(f.Time).ReadAsync(capture, args, default));
        Assert.False(File.Exists(Path.Combine(f.Home, "probe-record")));
    }

    [Fact]
    public async Task Publication_is_private_atomic_and_cancelled_replacement_preserves_original()
    {
        using var f = new CliCheckFixture();
        var capture = Capture(f); var file = Path.Combine(f.Home, "private-capture");
        await RemoteCliCapture.SaveAsync(file, capture, default);
        Assert.Equal(capture.InstallationId, RemoteCliCapture.Load(file).InstallationId);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        var bytes = File.ReadAllBytes(file);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteCliCapture.SaveAsync(file,
            capture with { InstallationId = Guid.NewGuid().ToString("N") }, cancelled.Token));
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Empty(Directory.GetFiles(f.Home, "*.tmp"));
    }

    [Fact]
    public async Task Npm_source_is_not_inferred_from_the_executable_name_or_version()
    {
        using var f = new CliCheckFixture(); var capture = Capture(f);
        f.SetPackage("wrong-package", f.Version);
        var result = await new RemoteCliCheck(f.Time).ReadAsync(capture, f.Args, default);
        Assert.Equal(f.Version, result.InstalledVersion); Assert.Equal("unknown", result.Source);
    }

    internal static RemoteCliCaptureData Capture(CliCheckFixture f)
    {
        var source = Path.Combine(f.Home, "custom-agents-source.json");
        File.WriteAllText(source, JsonSerializer.Serialize(f.Launcher.Agents, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return RemoteCliCapture.Capture(f.Launcher, "local-machine", Guid.NewGuid().ToString("N"), f.Home, source,
            new AgentCommand("/actual/owner/runtime", ["/actual/owner/helper.dll"], f.Home));
    }
}
