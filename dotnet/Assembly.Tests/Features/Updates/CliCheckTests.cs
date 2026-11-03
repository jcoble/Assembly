using System.Net;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Features.Updates;

public sealed class CliCheckTests
{
    [Theory]
    [InlineData("0.9.0", "0.10.0", "update_available")]
    [InlineData("10.0.0", "9.99.99", "ahead")]
    [InlineData("1.2.3+one", "1.2.3+two", "current")]
    [InlineData("999999999999999999999999.2.0", "1000000000000000000000000.0.0", "update_available")]
    public async Task Source_qualified_versions_compare_numeric_semver_not_text(string installed, string latest, string comparison)
    {
        using var f = new CliCheckFixture(version: installed); f.HttpHandler.Version = latest;
        var args = f.Args; var result = await f.Check(args);
        Assert.Equal(comparison, result.Comparison); Assert.Equal(args.MachineId, result.MachineId);
        Assert.Equal(args.ProviderId, result.ProviderId); Assert.Equal(args.AccountContext, result.AccountContext);
        Assert.Equal(args.ConfigurationTargetId, result.ConfigurationTargetId);
        Assert.Equal(installed, result.InstalledVersion); Assert.Equal(latest, result.Candidate!.Version);
        var json = JsonSerializer.Serialize(result, UpdatesJson.Default.CliCheckResult);
        Assert.DoesNotContain(f.Home, json); Assert.DoesNotContain("PRIVATE_ARG", json);
        Assert.DoesNotContain("/never/execute/adapter", json);
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("account")]
    [InlineData("target")]
    [InlineData("provider")]
    public async Task Wrong_selection_refuses_before_any_process(string change)
    {
        using var f = new CliCheckFixture(); var args = f.Args;
        args = change switch
        {
            "machine" => args with { MachineId = "remote-machine" },
            "account" => args with { AccountContext = "" },
            "target" => args with { ConfigurationTargetId = new string('a', 64) },
            _ => args with { ProviderId = "missing" }
        };
        await Assert.ThrowsAsync<CallFailed>(() => f.Check(args));
        Assert.False(File.Exists(Path.Combine(f.Home, "probe-record"))); Assert.Equal(0, f.Launcher.Launches);
    }

    [Theory]
    [InlineData("cli")]
    [InlineData("family")]
    [InlineData("env")]
    public async Task Configuration_change_while_latest_is_pending_invalidates_instead_of_redirecting(string change)
    {
        using var f = new CliCheckFixture(); f.HttpHandler.Gate = true;
        var checking = f.Check(); await f.HttpHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var before = f.Agent;
        f.Launcher.Agents = [change switch { "cli" => before with { Cli = "/different/absolute/cli" },
            "family" => before with { VendorFamily = "claude" }, _ => before with { Env = new Dictionary<string, string>() } }];
        f.HttpHandler.Release.TrySetResult();
        var error = await Assert.ThrowsAsync<CallFailed>(() => checking);
        Assert.Equal("selection_changed", error.Reason); Assert.Equal(0, f.Launcher.Launches);
    }

    [Fact]
    public async Task Installed_executable_replacement_before_acceptance_invalidates_its_result()
    {
        using var f = new CliCheckFixture(); f.HttpHandler.Gate = true;
        var checking = f.Check(); await f.HttpHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        File.AppendAllText(f.Entry, "\n# replaced installation\n"); f.HttpHandler.Release.TrySetResult();
        var error = await Assert.ThrowsAsync<CallFailed>(() => checking);
        Assert.Equal("selection_changed", error.Reason);
    }

    [Fact]
    public async Task Registry_failure_keeps_real_installed_version_but_never_claims_current()
    {
        using var f = new CliCheckFixture(); f.HttpHandler.Status = HttpStatusCode.ServiceUnavailable;
        var result = await f.Check(); Assert.Equal(f.Version, result.InstalledVersion);
        Assert.Null(result.Candidate); Assert.Equal("unverified", result.Comparison); Assert.Equal("release_unverified", result.Reason);
    }

    [Fact]
    public async Task Phone_and_missing_peer_cannot_select_a_local_CLI()
    {
        using var f = new CliCheckFixture(); var calls = new UpdateCalls(f.Launcher, "local-machine", f.Http, f.Time, default, (_, _) => null, new RemoteCliClient(f.Http, f.Time));
        await Assert.ThrowsAsync<CallFailed>(() => calls.CheckAsync(f.Args, new(default, "fixture", Peer: new PhonePeer("phone"))));
        await Assert.ThrowsAsync<CallFailed>(() => calls.CheckAsync(f.Args, new(default, "fixture")));
        Assert.False(File.Exists(Path.Combine(f.Home, "probe-record")));
    }

    [Fact]
    public void Caller_execution_fields_are_not_part_of_the_route_contract()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"machineId":"local-machine","providerId":"x","accountContext":"","configurationTargetId":"x","path":"/untrusted"}""", UpdatesJson.Default.CliCheckArgs));
    }
}
