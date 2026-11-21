using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Remote.Install;
using Assembly.Infrastructure.Updates;

namespace Assembly.Desktop.Settings;

/// <summary>
/// Pure backend derivation rules for <see cref="SettingsState"/>: row states, machine states, scrubbed failures and summary.
/// </summary>
internal static class SettingsStateRules
{
    internal static bool ResolveInstalled(
        bool isLocal,
        AgentDescriptor? localAgent,
        bool localDescriptorInvalid,
        UpdateItem? adapterUpdate,
        UpdateItem? cliUpdate,
        SettingsStateComposer.ProviderRow? providerRow,
        bool adapterMissingFromUpdates)
    {
        if (providerRow?.Installation == "not_installed" || adapterMissingFromUpdates)
            return false;
        if (providerRow?.Installation == "installed" || adapterUpdate?.Current is not null || cliUpdate?.Current is not null)
            return true;
        return isLocal && localAgent is not null && !localDescriptorInvalid;
    }

    internal static string? DeriveSignInState(
        SettingsStateComposer.ProviderRow? providerRow,
        SettingsStateComposer.SignInRow? activeSignIn)
    {
        if (activeSignIn is not null)
        {
            return activeSignIn.State == "waiting_for_person"
                ? ProviderSignInState.NeedsSignIn
                : ProviderSignInState.SigningIn;
        }
        return providerRow?.AuthState switch
        {
            "signed_in" => ProviderSignInState.SignedIn,
            "needs_sign_in" => ProviderSignInState.NeedsSignIn,
            "checking" => ProviderSignInState.Checking,
            "unverified" => ProviderSignInState.Unverified,
            "unsupported" => ProviderSignInState.Unsupported,
            _ => null
        };
    }

    internal static SettingsFailure? DeriveLastFailure(
        AgentDescriptor? localAgent,
        bool localDescriptorInvalid,
        SettingsStateComposer.ProviderRow? providerRow,
        SettingsStateComposer.SignInRow? activeSignIn,
        SettingsStateComposer.SignInRow? failedSignIn,
        bool adapterMissingFromUpdates)
    {
        if (localDescriptorInvalid && localAgent is not null)
            return FailureOf(localAgent.Invalid()!, prefix: null);

        if (providerRow?.Reason is { } providerReason
            && !(providerRow.AuthState == "signed_in" && providerReason == "interrupted"))
            return FailureOf(providerReason, prefix: null);

        if (activeSignIn is null
            && failedSignIn?.Reason is { } signInReason
            && providerRow?.AuthState != "signed_in"
            && failedSignIn.UpdatedMs >= (providerRow?.CheckedMs ?? 0))
            return FailureOf(signInReason, prefix: "sign_in:");

        if (providerRow?.Installation == "not_installed" || adapterMissingFromUpdates)
            return FailureOf("not_installed", prefix: null);

        if (providerRow?.AuthState == "unsupported")
            return FailureOf("unsupported", prefix: null);

        return null;
    }

    internal static string? DeriveProviderMachineState(
        SettingsMachine machine,
        string? mirrorState,
        SettingsStateComposer.ProviderRow? providerRow,
        SettingsStateComposer.SignInRow? activeSignIn,
        bool explicitlyNotInstalled,
        bool localDescriptorInvalid,
        SettingsFailure? lastFailure)
    {
        if (machine.ConnectionState is MachineConnectionState.Offline or MachineConnectionState.Disconnected)
            return null;

        var nothingKnown = providerRow is null
            && activeSignIn is null
            && !explicitlyNotInstalled
            && !localDescriptorInvalid
            && lastFailure is null;
        if (machine.ConnectionState == MachineConnectionState.Checking && nothingKnown && !machine.Machine.Updating)
            return null;

        if (machine.Machine.Updating
            || mirrorState == "snapshotting"
            || providerRow?.AuthState == "checking"
            || activeSignIn?.State is "starting" or "finishing")
            return ProviderRowState.SettingUp;

        if (explicitlyNotInstalled
            || localDescriptorInvalid
            || providerRow?.Reason is "not_installed" or "cannot_start" or "invalid_descriptor")
            return ProviderRowState.Problem;

        if (activeSignIn?.State == "waiting_for_person" || providerRow?.AuthState == "needs_sign_in")
            return ProviderRowState.NeedsSignIn;

        if (lastFailure is not null || providerRow?.AuthState is "could_not_check" or "unsupported")
            return ProviderRowState.Problem;

        return ProviderRowState.Ready;
    }

    internal static IReadOnlyList<ModelChoice>? DeriveSharedModels(IReadOnlyList<SettingsProviderMachine> machines)
    {
        IReadOnlyList<ModelChoice>? shared = null;
        foreach (var machine in machines)
        {
            if (machine.Models is not { Count: > 0 } models)
                continue;
            if (shared is null)
                shared = models;
            else if (!shared.SequenceEqual(models))
                return null;
        }
        return shared;
    }

    internal static string DeriveAggregateProviderState(IReadOnlyList<SettingsProviderMachine> machines)
    {
        // Only machines with a known (non-null) state contribute; offline/disconnected remotes are skipped.
        // The local machine is always present and connected, so an enabled provider always has at least
        // one non-null machine state.
        if (machines.Any(m => m.State == ProviderRowState.Problem))
            return ProviderRowState.Problem;
        if (machines.Any(m => m.State == ProviderRowState.NeedsSignIn))
            return ProviderRowState.NeedsSignIn;
        if (machines.Any(m => m.State == ProviderRowState.SettingUp))
            return ProviderRowState.SettingUp;
        return ProviderRowState.Ready;
    }

    internal static string DeriveConnectionState(MachineItem item)
    {
        if (item.Kind == "local")
            return MachineConnectionState.Connected;
        if (!item.Checked)
            return MachineConnectionState.Checking;
        if (item.Reachable)
            return MachineConnectionState.Connected;
        return string.Equals(item.Reason, "disconnected", StringComparison.Ordinal)
            ? MachineConnectionState.Disconnected
            : MachineConnectionState.Offline;
    }

    internal static bool IsWaitingOnSessions(MachineItem item) =>
        (item.Updating && IsWaitingWords(item.UpdateNote))
        || (item.Restarting && IsWaitingWords(item.RestartNote));

    private static bool IsWaitingWords(string? note) =>
        note is not null && (
            note.Contains(AppUpdate.WaitingWords, StringComparison.Ordinal)
            || note.Contains(RemoteReleaseInstall.WaitingWords, StringComparison.Ordinal)
            || note.Contains("waiting for running sessions", StringComparison.OrdinalIgnoreCase));

    internal static string DeriveUpdateState(MachineItem item, bool waitingOnSessions)
    {
        if (waitingOnSessions)
            return MachineUpdateState.WaitingOnSessions;
        if (item.Restarting)
            return MachineUpdateState.Restarting;
        if (item.Updating)
            return MachineUpdateState.Updating;
        if (item.UpdateNote is not null || item.RestartNote is not null)
            return MachineUpdateState.Failed;
        if (item.Name is null && item.RestartReady is not null)
            return MachineUpdateState.RestartRequired;
        if (item.CantUpdate is not null)
            return MachineUpdateState.CantUpdate;
        if (HasOfferedBehind(item))
            return MachineUpdateState.UpdateAvailable;
        if (item.Updates is { Count: > 0 } && item.Updates.All(u => u.State != UpdateItem.Unknown))
            return MachineUpdateState.UpToDate;
        return MachineUpdateState.Unknown;
    }

    private static bool HasOfferedBehind(MachineItem item) =>
        item.Updates is { Count: > 0 } updates && updates.Any(update =>
            update.State == UpdateItem.Behind && !(item.RestartReady is not null && update.Kind == "desktop"));

    internal static SettingsSummary DeriveSummary(
        IReadOnlyList<SettingsProvider> providers,
        IReadOnlyList<SettingsMachine> machines)
    {
        var machineLabels = machines.ToDictionary(m => m.Key, m => m.DisplayName, StringComparer.Ordinal);

        // 1. Problem: provider problem or update/restart failure; offline/disconnected machines are skipped.
        var problemProviders = providers.Where(p => p.Enabled && p.State == ProviderRowState.Problem).ToArray();
        if (problemProviders.Length == 1)
        {
            var provider = problemProviders[0];
            var badMachine = provider.Machines.FirstOrDefault(m => m.State == ProviderRowState.Problem);
            var where = badMachine is not null && machineLabels.TryGetValue(badMachine.MachineKey, out var label)
                ? $" on {label}"
                : "";
            return new SettingsSummary(SettingsSummary.Problem, $"{provider.Name} has a problem{where}.");
        }
        if (problemProviders.Length > 1)
            return new SettingsSummary(SettingsSummary.Problem, $"{problemProviders.Length} providers have problems.");

        var failedMachine = machines.FirstOrDefault(m => m.UpdateState == MachineUpdateState.Failed);
        if (failedMachine is not null)
        {
            var action = failedMachine.Machine.RestartNote is not null && failedMachine.Machine.UpdateNote is null
                ? "Restart"
                : "Update";
            return new SettingsSummary(SettingsSummary.Problem, $"{action} failed on {failedMachine.DisplayName}.");
        }

        // 2. Needs sign-in.
        var signInProviders = providers.Where(p => p.Enabled && p.State == ProviderRowState.NeedsSignIn).ToArray();
        if (signInProviders.Length == 1)
        {
            var provider = signInProviders[0];
            var needing = provider.Machines.Where(m => m.State == ProviderRowState.NeedsSignIn).ToArray();
            if (needing.Length == 1 && machineLabels.TryGetValue(needing[0].MachineKey, out var label))
                return new SettingsSummary(SettingsSummary.NeedsSignIn, $"{provider.Name} needs sign-in on {label}.");
            if (needing.Length > 1)
                return new SettingsSummary(SettingsSummary.NeedsSignIn, $"{provider.Name} needs sign-in on {needing.Length} machines.");
            return new SettingsSummary(SettingsSummary.NeedsSignIn, $"{provider.Name} needs sign-in.");
        }
        if (signInProviders.Length > 1)
            return new SettingsSummary(SettingsSummary.NeedsSignIn, $"{signInProviders.Length} providers need sign-in.");

        // 3. Waiting on running sessions.
        var waitingMachines = machines.Where(m => m.WaitingOnSessions).ToArray();
        if (waitingMachines.Length == 1)
            return new SettingsSummary(SettingsSummary.WaitingOnSessions, $"{waitingMachines[0].DisplayName} is waiting for running sessions to finish.");
        if (waitingMachines.Length > 1)
            return new SettingsSummary(SettingsSummary.WaitingOnSessions, $"{waitingMachines.Length} machines are waiting for running sessions to finish.");

        // 4. Restart to finish.
        var local = machines.FirstOrDefault(m => m.Machine.Name is null);
        if (local?.Machine.RestartReady is { } readyVersion)
            return new SettingsSummary(SettingsSummary.RestartToFinish, $"Restart Assembly to finish updating to {readyVersion}.");

        // 5. Nothing needs the user.
        return new SettingsSummary(SettingsSummary.Nothing, "Everything is ready.");
    }

    private static SettingsFailure FailureOf(string rawCode, string? prefix)
    {
        var safeCode = ScrubSafeReason(rawCode);
        var message = safeCode == "unsupported"
            ? "Sign-in is not supported for this provider."
            : ProviderProblems.Text(ParseProblem(safeCode));
        var detail = prefix is null ? safeCode : prefix + safeCode;
        return new SettingsFailure(message, detail);
    }

    // Only fixed allowlisted reason codes may ever leave the backend as failure detail.
    private static string ScrubSafeReason(string? code) => code switch
    {
        "not_installed" => "not_installed",
        "cannot_start" => "cannot_start",
        "invalid_descriptor" => "invalid_descriptor",
        "timed_out" => "timed_out",
        "adapter_crashed" => "adapter_crashed",
        "network" => "network",
        "version_unknown" => "version_unknown",
        "interrupted" => "interrupted",
        "unsupported" => "unsupported",
        _ => "adapter_error"
    };

    private static ProviderProblem ParseProblem(string safeCode) => safeCode switch
    {
        "not_installed" => ProviderProblem.not_installed,
        "cannot_start" => ProviderProblem.cannot_start,
        "invalid_descriptor" => ProviderProblem.invalid_descriptor,
        "timed_out" => ProviderProblem.timed_out,
        "adapter_crashed" => ProviderProblem.adapter_crashed,
        "network" => ProviderProblem.network,
        "version_unknown" => ProviderProblem.version_unknown,
        "interrupted" => ProviderProblem.interrupted,
        _ => ProviderProblem.adapter_error
    };
}
