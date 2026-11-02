using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Features.Updates;

public sealed class RemoteCliClientTests
{
    [Fact]
    public async Task Fixed_request_and_real_helper_readback_produce_exact_selected_comparison_without_backend()
    {
        using var f = new CliCheckFixture();
        var binding = await Binding(f);
        var client = Client(f);
        var result = await client.CheckAsync(binding, f.Args, () => binding, default, default);
        Assert.Equal(f.Args.MachineId, result.MachineId); Assert.Equal(f.Args.ProviderId, result.ProviderId);
        Assert.Equal(f.Args.AccountContext, result.AccountContext); Assert.Equal(f.Args.ConfigurationTargetId, result.ConfigurationTargetId);
        Assert.Equal("0.9.0", result.InstalledVersion); Assert.Equal("0.10.0", result.Candidate!.Version);
        Assert.Equal("update_available", result.Comparison); Assert.Null(result.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Profile_or_generation_replacement_after_actual_readback_cannot_accept_the_old_result(bool generationOnly)
    {
        using var f = new CliCheckFixture(); var binding = await Binding(f);
        var current = binding;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = Client(f, async () => { entered.SetResult(); await release.Task; })
            .CheckAsync(binding, f.Args, () => current, default, default);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await entered.Task.WaitAsync(bound.Token);
        current = generationOnly ? binding with { ProfileGeneration = new string('2', 32) }
            : binding with { Profile = binding.Profile with { Ssh = "changed-profile" } };
        release.SetResult();
        Assert.Equal("selection_changed", (await Assert.ThrowsAsync<CallFailed>(() => read)).Reason);
        Assert.True(File.Exists(Path.Combine(f.Home, "probe-record")));
    }

    [Fact]
    public async Task Actual_readback_for_another_installation_is_refused()
    {
        using var f = new CliCheckFixture(); var binding = await Binding(f);
        binding = binding with { InstallationId = Guid.NewGuid().ToString("N") };
        Assert.Equal("invalid_remote_result", (await Assert.ThrowsAsync<CallFailed>(() =>
            Client(f).CheckAsync(binding, f.Args, () => binding, default, default))).Reason);
        Assert.True(f.HttpHandler.Entered.Task.IsCompleted);
    }

    [Fact]
    public async Task Safe_selection_changed_refusal_is_preserved_by_the_fixed_result_contract()
    {
        using var f = new CliCheckFixture(); var binding = await Binding(f);
        var args = f.Args;
        var response = new RemoteCliReadResult(binding.MachineId, binding.InstallationId, args.ProviderId,
            args.AccountContext, args.ConfigurationTargetId, null, "unknown", "unknown", "selection_changed");
        var helper = Path.Combine(f.Home, "refusing-helper");
        CliCheckFixture.WriteExecutable(helper, "my $line=<STDIN>; print q~" +
            JsonSerializer.Serialize(response, RemoteCliJson.Default.RemoteCliReadResult) + "~;");
        binding = binding with { HelperCommand = new(helper, [], f.Home) };
        var result = await Client(f).CheckAsync(binding, args, () => binding, default, default);
        Assert.Equal("selection_changed", result.Reason); Assert.Equal("unverified", result.Comparison);
    }

    [Fact]
    public async Task One_overall_deadline_covers_metadata_and_the_remaining_remote_probe()
    {
        using var f = new CliCheckFixture(); var binding = await Binding(f);
        f.HttpHandler.Gate = true; var processes = 0;
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RemoteCliClient(f.Http, f.Time, async (_, caller, _) =>
        {
            processes++; admitted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, caller);
            return [];
        });
        var read = client.CheckAsync(binding, f.Args, () => binding, default, default);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await f.HttpHandler.Entered.Task.WaitAsync(bound.Token);
        f.Time.Advance(TimeSpan.FromSeconds(10));
        await admitted.Task.WaitAsync(bound.Token);
        Assert.False(read.IsCompleted);
        f.Time.Advance(TimeSpan.FromSeconds(10));
        var result = await read.WaitAsync(bound.Token);
        Assert.Equal("timeout", result.Reason); Assert.Equal("unverified", result.Comparison);
        Assert.Null(result.InstalledVersion); Assert.Equal(1, processes);
    }

    private static RemoteCliClient Client(CliCheckFixture f, Func<Task>? afterRead = null)
    {
        // A controlled SSH process harness executes the generated fixed command locally.
        // This exercises the actual helper/protocol/lifetime, but is not a real remote-host acceptance claim.
        var ssh = Path.Combine(f.Home, "ssh-harness");
        CliCheckFixture.WriteExecutable(ssh, "exec('/bin/sh', '-c', $ARGV[-1]) or die;");
        return new(f.Http, TimeProvider.System, async (invocation, caller, host) =>
        {
            Assert.Equal("ssh", invocation.Command.FileName);
            Assert.Contains("workbox-test", invocation.Command.Arguments);
            var bytes = await new SshUpdateProcess(TimeProvider.System).ReadAsync(invocation with
            { Command = invocation.Command with { FileName = ssh } }, caller, host);
            if (afterRead is not null) await afterRead();
            return bytes;
        });
    }

    private static async Task<RemoteCliBinding> Binding(CliCheckFixture f)
    {
        var capture = RemoteCliCaptureTests.Capture(f); var file = Path.Combine(f.Home, "private-capture");
        await RemoteCliCapture.SaveAsync(file, capture, default);
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
            Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator).Select(p => Path.Combine(p, "dotnet")).First(File.Exists);
        return new(new("selected", "workbox-test", 12345), capture.MachineId, capture.InstallationId, file,
            new AgentCommand(dotnet, [typeof(Assembly.Remote.Serve.ServeOptions).Assembly.Location], f.Home),
            f.Family, f.Args.ProviderId, f.Args.AccountContext, f.Args.ConfigurationTargetId, new string('1', 32));
    }
}
