using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Agents;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class ProviderRuntimeBinaryTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("assembly-runtime-binary-").FullName;
    private string Record => Path.Combine(_home, "launch-record");

    private AgentDescriptor Descriptor(string family, string? binary)
    {
        var script = Path.Combine(_home, "fake-adapter.sh");
        File.WriteAllText(script, """
            printf '%s\n' "${CODEX_PATH-unset}" "${CLAUDE_CODE_EXECUTABLE-unset}" "$ASSEMBLY_RUNTIME_CAPTURED" "$@" > "$ASSEMBLY_RUNTIME_RECORD"
            """);
        return new("selected", "Selected", "/bin/sh", [script, "literal argument", "$(literal)", ""],
            new Dictionary<string, string>
            {
                ["CODEX_PATH"] = "/fixture/captured-codex",
                ["CLAUDE_CODE_EXECUTABLE"] = "/fixture/captured-claude",
                ["ASSEMBLY_RUNTIME_CAPTURED"] = "frozen-value",
                ["ASSEMBLY_RUNTIME_RECORD"] = Record
            }, Cli: binary, VendorFamily: family);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public async Task Selected_binary_overrides_only_its_private_adapter_input_preserving_command_arguments_and_capture(string family)
    {
        var descriptor = Descriptor(family, "/fixture/selected binary");
        var launcher = new AdapterLauncher([descriptor], _home, new UnclaimedPermissions(), _ => _home);
        var captured = ConfigTargetIdentity.Capture(launcher, descriptor.Id);
        var snapshot = captured.Environment.OrderBy(pair => pair.Key).ToArray();

        await using (var link = await launcher.LaunchConfiguredAsync(captured.Descriptor, captured.Environment,
            new StageTimes(TimeProvider.System), default))
            await link.Exited.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([
            family == "codex" ? "/fixture/selected binary" : "/fixture/captured-codex",
            family == "claude" ? "/fixture/selected binary" : "/fixture/captured-claude",
            "frozen-value", "literal argument", "$(literal)", ""
        ], File.ReadAllLines(Record));
        Assert.Equal(snapshot, captured.Environment.OrderBy(pair => pair.Key).ToArray());
        Assert.Equal("/bin/sh", captured.Descriptor.Command);
        Assert.Equal(descriptor.Args, captured.Descriptor.Args);
    }

    [Theory]
    [InlineData("codex", null)]
    [InlineData("codex", "")]
    [InlineData("claude", null)]
    [InlineData("claude", "")]
    [InlineData("other-family", "/fixture/unsupported-selection")]
    public async Task Unset_empty_and_unmapped_binary_metadata_leave_the_frozen_environment_unchanged(string family, string? binary)
    {
        var descriptor = Descriptor(family, binary);
        var launcher = new AdapterLauncher([descriptor], _home, new UnclaimedPermissions(), _ => _home);
        var captured = ConfigTargetIdentity.Capture(launcher, descriptor.Id);

        await using (var link = await launcher.LaunchConfiguredAsync(captured.Descriptor, captured.Environment,
            new StageTimes(TimeProvider.System), default))
            await link.Exited.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["/fixture/captured-codex", "/fixture/captured-claude", "frozen-value", "literal argument", "$(literal)", ""],
            File.ReadAllLines(Record));
    }

    [Fact]
    public async Task A_stale_captured_binary_never_starts_the_disposable_adapter()
    {
        var descriptor = Descriptor("codex", "/fixture/original");
        var configured = new List<AgentDescriptor> { descriptor };
        var launcher = new AdapterLauncher(configured, _home, new UnclaimedPermissions(), _ => _home);
        var captured = ConfigTargetIdentity.Capture(launcher, descriptor.Id);
        configured[0] = descriptor with { Cli = "/fixture/replacement" };

        await Assert.ThrowsAsync<CallFailed>(() => launcher.LaunchConfiguredAsync(captured.Descriptor, captured.Environment,
            new StageTimes(TimeProvider.System), default));

        Assert.False(File.Exists(Record));
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);
}
