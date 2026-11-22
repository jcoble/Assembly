using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Desktop.Serve;
using Assembly.Desktop.Settings;
using Assembly.Infrastructure.Persistence;
using Assembly.Tests.Features.Chat.Sessions;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Features.Settings;

public sealed partial class ProviderReconcilerSocketTests
{
    private sealed class UnavailableCheckSource : IUpdateSource
    {
        public Task<UpdateWanted> WantedAsync(CancellationToken ct, bool fresh = false) =>
            throw new CallFailed("check unavailable (fixture)", "check_unavailable");

        public bool StartsRemoteUpdates(string machine) => false;

        public Task InstallAsync(string machine, Action<string> progress, CancellationToken ct) => Task.CompletedTask;

        public Task UpdateAsync(IReadOnlyList<MachineBehind> machines, bool automatic, Action<string> progress, CancellationToken ct) =>
            Task.CompletedTask;
    }

    // Awaiting a check is its own summary, not "Everything is ready"; and when the reconcile that
    // should have started the check fails, the provider is a problem with a Try again, not stuck.
    [Fact]
    public async Task A_reconcile_that_cannot_check_leaves_the_provider_a_problem_not_setting_up_forever()
    {
        var tempRoot = Directory.CreateTempSubdirectory("assembly-reconcile-nocheck-").FullName;
        var home = Directory.CreateDirectory(Path.Combine(tempRoot, "desktop")).FullName;
        var launcher = new FakeLauncher
        {
            Agents =
            [
                new("claude", "Claude Code", "claude-agent-acp", [], null, VendorFamily: "claude"),
                new("codex", "Codex", "codex-acp", [], null, VendorFamily: "codex"),
            ]
        };

        try
        {
            var homeLock = HomeLock.Take(home);
            await using var backend = await DesktopBackend.StartAsync(
                homeLock,
                home,
                Token,
                port: 0,
                launcher,
                TextWriter.Null,
                remote: null,
                tunnel: null,
                utilityClis: null,
                savedRemote: null,
                engineEffects: null,
                engineStarted: null,
                agentsFile: null,
                loginShell: _ => Task.FromResult<(string?, string?)>((null, null)),
                programTerminals: null,
                givenInstall: null,
                givenUpdateSource: new UnavailableCheckSource());

            using var stateSocket = await OpenSocketAsync(backend.Address);
            using var cmdSocket = await OpenSocketAsync(backend.Address);
            await SendAsync(stateSocket, new { t = "sub", id = 1, topic = SettingsStateFeature.Topic });
            var initial = await WaitForStateAsync(stateSocket, state =>
                state.GetProperty("providers").EnumerateArray().Any(p => p.GetProperty("id").GetString() == "claude"));
            Assert.Equal(SettingsSummary.SettingUp, initial.GetProperty("summary").GetProperty("kind").GetString());

            await SendAsync(cmdSocket, new
            {
                t = "call",
                id = 2,
                feature = LocalProviderReconciler.FeatureName,
                method = LocalProviderReconciler.ReconcileMethod,
                args = new { machine = "local" }
            });
            Assert.Equal("result", (await ReadAsync(cmdSocket)).GetProperty("t").GetString());

            var settled = await WaitForStateAsync(stateSocket, state =>
            {
                var local = ProviderLocal(state, "claude");
                return local.GetProperty("state").GetString() == ProviderRowState.Problem
                    && local.GetProperty("lastFailure").ValueKind == JsonValueKind.Object;
            });
            Assert.Equal(SettingsSummary.Problem, settled.GetProperty("summary").GetProperty("kind").GetString());
            var failure = ProviderLocal(settled, "claude").GetProperty("lastFailure");
            Assert.False(string.IsNullOrWhiteSpace(failure.GetProperty("message").GetString()));
            Assert.Equal(SettingsFailure.RetryCheck, failure.GetProperty("retry").GetString());

            stateSocket.Abort();
            cmdSocket.Abort();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static JsonElement ProviderLocal(JsonElement state, string providerId) =>
        state.GetProperty("providers").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == providerId)
            .GetProperty("machines").EnumerateArray()
            .Single(m => m.GetProperty("machineKey").GetString() == "local");
}
