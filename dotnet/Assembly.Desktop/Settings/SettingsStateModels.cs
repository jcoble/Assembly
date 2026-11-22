using System.Text.Json.Serialization;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Agents;

namespace Assembly.Desktop.Settings;

/// <summary>The single composed state pushed on the <c>settings_state</c> topic for the Home panel.</summary>
internal sealed record SettingsState(
    SettingsSummary Summary,
    IReadOnlyList<SettingsProvider> Providers,
    IReadOnlyList<SettingsMachine> Machines);

/// <summary>
/// One top-level summary of what needs the user right now, plus whether Assembly is working on its own
/// (<see cref="Working"/>, for the Home button's progress ring) and whether the user is needed
/// (<see cref="NeedsUser"/>, for turning the Home button amber). <see cref="RestartVersion"/> travels
/// alongside every kind so a staged restart is never hidden by another need.
/// </summary>
internal sealed record SettingsSummary(string Kind, string Message, bool Working = false, bool NeedsUser = false, string? RestartVersion = null)
{
    internal const string Nothing = "nothing";
    internal const string SettingUp = "setting_up";
    internal const string RestartToFinish = "restart_to_finish";
    internal const string WaitingOnSessions = "waiting_on_sessions";
    internal const string NeedsSignIn = "needs_sign_in";
    internal const string Problem = "problem";
}

/// <summary>
/// The backend-derived row states shared by a provider and each of its machines. A remote with no
/// recorded provider facts is <see cref="NotChecked"/>: nothing here can start its first check yet
/// (that origination is the later remote-reconcile work), so it is never shown as setting up.
/// </summary>
internal static class ProviderRowState
{
    internal const string Ready = "ready";
    internal const string SettingUp = "setting_up";
    internal const string NeedsSignIn = "needs_sign_in";
    internal const string Problem = "problem";
    internal const string NotChecked = "not_checked";
}

/// <summary>Safe sign-in state values derived from engine provider and sign-in rows.</summary>
internal static class ProviderSignInState
{
    internal const string SignedIn = "signed_in";
    internal const string NeedsSignIn = "needs_sign_in";
    internal const string SigningIn = "signing_in";
    internal const string Checking = "checking";
    internal const string Unverified = "unverified";
    internal const string Unsupported = "unsupported";
}

/// <summary>Backend-derived connection states for a machine.</summary>
internal static class MachineConnectionState
{
    internal const string Connected = "connected";
    internal const string Checking = "checking";
    internal const string Offline = "offline";
    internal const string Disconnected = "disconnected";
}

/// <summary>Backend-derived update states for a machine.</summary>
internal static class MachineUpdateState
{
    internal const string UpToDate = "up_to_date";
    internal const string Unknown = "unknown";
    internal const string UpdateAvailable = "update_available";
    internal const string Updating = "updating";
    internal const string WaitingOnSessions = "waiting_on_sessions";
    internal const string RestartRequired = "restart_required";
    internal const string Restarting = "restarting";
    internal const string Failed = "failed";
    internal const string CantUpdate = "cant_update";
}

/// <summary>One provider in the settings panel, whether it is switched on, its derived row state, and one entry per machine.</summary>
internal sealed record SettingsProvider(
    string Id,
    string Name,
    string Glyph,
    string? VendorFamily,
    bool Enabled,
    string? State,
    IReadOnlyList<ModelChoice>? Models,
    IReadOnlyList<SettingsProviderMachine> Machines);

/// <summary>One provider's composed state on one machine.</summary>
internal sealed record SettingsProviderMachine(
    string MachineKey,
    string? EngineMachine,
    string? State,
    bool Installed,
    string? AdapterVersion,
    string? CliVersion,
    string? SignInState,
    IReadOnlyList<ModelChoice>? Models,
    SettingsFailure? LastFailure,
    string? AccountContext,
    string? ProviderTargetId,
    long? CheckedMs,
    long Version);

/// <summary>
/// Last safe failure for a provider on a machine: one plain sentence, the detail for its Details
/// view (scrubbed raw output where this machine has it, a plain sentence otherwise — never a code),
/// and what Try again must retry: a check, or the install/update itself. An install failure also
/// records which operation, machine and tool failed, so blame and retry stay on that one target.
/// </summary>
internal sealed record SettingsFailure(string Message, string Detail, string Retry = SettingsFailure.RetryCheck,
    string? Operation = null, string? MachineKey = null, string? Tool = null)
{
    internal const string RetryCheck = "check";
    internal const string RetryInstall = "install";
}

/// <summary>One machine with its existing <see cref="MachineItem"/> plus backend-derived status fields.</summary>
internal sealed record SettingsMachine(
    string Key,
    string DisplayName,
    string? EngineMachine,
    string ConnectionState,
    string UpdateState,
    bool WaitingOnSessions,
    MachineItem Machine);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SettingsState))]
[JsonSerializable(typeof(SettingsSummary))]
internal sealed partial class SettingsStateJson : JsonSerializerContext;
