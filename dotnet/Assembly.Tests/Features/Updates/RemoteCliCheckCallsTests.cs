using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Features.Updates;

public sealed class RemoteCliCheckCallsTests
{
    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public async Task Selected_remote_uses_saved_SSH_and_actual_remote_capture_without_any_local_CLI_execution(string family)
    {
        using var f = await Fixture.Create(family);
        var result = await f.Check();
        Assert.Equal("update_available", result.Comparison);
        Assert.Equal(f.Remote.Version, result.InstalledVersion);
        Assert.Equal(f.Args.MachineId, result.MachineId);
        Assert.Equal(f.Args.ProviderId, result.ProviderId);
        Assert.Equal(f.Args.AccountContext, result.AccountContext);
        Assert.Equal(f.Args.ConfigurationTargetId, result.ConfigurationTargetId);
        Assert.Equal(1, f.RemoteReads);
        Assert.True(File.Exists(Path.Combine(f.Remote.Home, "probe-record")));
        f.AssertNoLocalExecution();
        var json = JsonSerializer.Serialize(result, UpdatesJson.Default.CliCheckResult);
        Assert.DoesNotContain(f.Remote.Home, json); Assert.DoesNotContain("PRIVATE_ARG", json);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Profile_replacement_during_remote_probe_refuses_old_result_even_when_saved_fields_are_identical(bool identical)
    {
        using var f = await Fixture.Create();
        f.AfterRead = () =>
        {
            f.Store.Import(identical ? f.Profile : f.Profile with { Ssh = "replacement-test-profile" });
            f.Publish();
        };
        var error = await Assert.ThrowsAsync<CallFailed>(() => f.Check());
        Assert.Equal("selection_changed", error.Reason);
        Assert.Equal(1, f.RemoteReads); f.AssertNoLocalExecution();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("wrong-machine")]
    public async Task Missing_or_unmatched_private_authority_never_falls_back_to_local_or_queries_registry(string unavailable)
    {
        using var f = await Fixture.Create();
        if (unavailable == "missing") f.Store.Import(f.Profile);
        if (unavailable == "empty") { f.Locator = f.Locator with { Targets = [] }; f.Publish(); }
        if (unavailable == "wrong-machine") { f.Locator = f.Locator with { MachineId = "another-engine" }; f.Publish(); }
        var error = await Assert.ThrowsAsync<CallFailed>(() => f.Check());
        Assert.Equal("configuration_unavailable", error.Reason);
        Assert.Equal(0, f.RemoteReads); Assert.False(f.Remote.HttpHandler.Entered.Task.IsCompleted);
        f.AssertNoLocalExecution();
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("account")]
    [InlineData("configuration")]
    public async Task A_different_remote_target_cannot_reuse_the_selected_profile_or_local_configuration(string selection)
    {
        using var f = await Fixture.Create();
        var args = selection switch
        {
            "provider" => f.Args with { ProviderId = "unconfigured" },
            "account" => f.Args with { AccountContext = "" },
            _ => f.Args with { ConfigurationTargetId = new string('a', 64) }
        };
        await Assert.ThrowsAsync<CallFailed>(() => f.Check(args));
        Assert.Equal(0, f.RemoteReads); Assert.False(f.Remote.HttpHandler.Entered.Task.IsCompleted);
        f.AssertNoLocalExecution();
    }

    [Fact]
    public async Task A_closed_request_cancels_the_remote_read_and_preserves_the_saved_locator()
    {
        using var f = await Fixture.Create();
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.WaitRead = async ct => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
        var check = f.Check(closed: stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(8)); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Equal(f.Locator.InstallationId, f.Store.GetCliLocator(f.Profile.Name, default)!.Locator.InstallationId);
        f.AssertNoLocalExecution();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDatabase _file = new();
        private readonly AppDatabase _db;
        internal CliCheckFixture Local { get; } = new();
        internal CliCheckFixture Remote { get; }
        internal SavedMachine Profile { get; } = new("saved-remote", "workbox-test", 12345, "never-read-this-token");
        internal MachineStore Store { get; }
        internal RemoteCliLocator Locator { get; set; } = null!;
        internal RemoteCliCaptureData Capture { get; private set; } = null!;
        internal CliCheckArgs Args => Remote.Args with { MachineId = "remote-engine-id" };
        internal int RemoteReads { get; private set; }
        internal Action? AfterRead { get; set; }
        internal Func<CancellationToken, Task>? WaitRead { get; set; }
        private Fixture(string family)
        {
            Remote = new(family); _db = TestDatabase.Open(_file.Path); Store = new(_db); Store.Import(Profile);
        }
        internal static async Task<Fixture> Create(string family = "codex")
        {
            var f = new Fixture(family);
            f.Capture = RemoteCliCaptureTests.Capture(f.Remote) with { MachineId = f.Args.MachineId };
            var file = Path.Combine(f.Remote.Home, "published-capture");
            await RemoteCliCapture.SaveAsync(file, f.Capture, default);
            f.Locator = RemoteCliCapture.Locator(f.Capture, file); f.Publish();
            return f;
        }
        internal void Publish()
        {
            var expected = Store.CaptureProfile(Profile.Name, default)!;
            Assert.True(_db.Writer.Write("test.publish", scope => Store.SetCliLocator(scope, expected, Locator)));
        }
        internal Task<CliCheckResult> Check(CliCheckArgs? args = null, CancellationToken closed = default)
        {
            var client = new RemoteCliClient(Remote.Http, Remote.Time, async (invocation, caller, _) =>
            {
                RemoteReads++;
                Assert.Equal("ssh", invocation.Command.FileName);
                Assert.Contains(Profile.Ssh, invocation.Command.Arguments);
                Assert.DoesNotContain(Profile.Port.ToString(), invocation.Command.Arguments);
                Assert.DoesNotContain(Profile.TokenFile, invocation.Command.Arguments);
                if (WaitRead is not null) await WaitRead(caller);
                var request = JsonSerializer.Deserialize(invocation.Request!, UpdatesJson.Default.CliCheckArgs)!;
                var result = await new RemoteCliCheck(Remote.Time).ReadAsync(Capture, request, caller);
                AfterRead?.Invoke();
                return JsonSerializer.SerializeToUtf8Bytes(result, RemoteCliJson.Default.RemoteCliReadResult);
            });
            var calls = new UpdateCalls(Local.Launcher, "local-machine", Remote.Http, Remote.Time, default,
                (machine, ct) => machine == Args.MachineId ? Store.GetCliLocator(Profile.Name, ct) : null, client);
            return calls.CheckAsync(args ?? Args, new(default, "fixture", Closed: closed, Peer: new OwnerPeer()));
        }
        internal void AssertNoLocalExecution()
        {
            Assert.False(File.Exists(Path.Combine(Local.Home, "probe-record")));
            Assert.False(Local.HttpHandler.Entered.Task.IsCompleted); Assert.Equal(0, Local.Launcher.Launches);
        }
        public void Dispose() { Local.Dispose(); Remote.Dispose(); _db.Dispose(); _file.Dispose(); }
    }
}
