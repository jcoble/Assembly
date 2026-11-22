using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Agents;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Desktop.Settings;

/// <summary>
/// Composes the single <see cref="SettingsState"/> view from existing machine, adapter and engine state.
/// Reads only; installs nothing, probes nothing and writes nothing.
/// </summary>
internal sealed class SettingsStateComposer(
    IAppDatabase db,
    IAgentLauncher launcher,
    MachinesFeature machinesFeature,
    string localEngineMachine,
    TimeProvider time,
    ProviderFailureDetails? failureDetails = null,
    Func<bool>? isReconciling = null)
{
    private const string LocalKey = "local";

    // A local check counts as evidence for Ready only when it was done in this run of the backend.
    private readonly long _runStartMs = time.GetUtcNow().ToUnixTimeMilliseconds();

    internal sealed record ProviderRow(
        string Machine,
        string Provider,
        string AccountContext,
        string Installation,
        string AuthState,
        string? Reason,
        string? FactsJson,
        long? CheckedMs,
        long Version);

    internal sealed record SignInRow(
        string Machine,
        string Provider,
        string AccountContext,
        string State,
        string? Reason,
        long DeadlineMs,
        long UpdatedMs);

    private sealed record MirrorRow(string Machine, string Name, string State);

    private sealed record EngineSnapshotData(
        Dictionary<string, MirrorRow> MirrorsByName,
        List<ProviderRow> Providers,
        List<SignInRow> SignIns,
        IReadOnlyDictionary<string, bool> SavedEnabled);

    internal SettingsState Compose(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var nowMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        var machineItems = machinesFeature.Snapshot(ct);
        var localAgents = launcher.Agents.ToArray();
        var engineData = ReadEngineData(ct);

        var machines = new List<SettingsMachine>(machineItems.Count);
        foreach (var item in machineItems)
        {
            var key = item.Name ?? LocalKey;
            var engineMachine = item.Name is null
                ? localEngineMachine
                : engineData.MirrorsByName.GetValueOrDefault(item.Name)?.Machine;
            var waitingOnSessions = SettingsStateRules.IsWaitingOnSessions(item);
            machines.Add(new SettingsMachine(
                Key: key,
                DisplayName: item.Name ?? LocalDisplayName(),
                EngineMachine: engineMachine,
                ConnectionState: SettingsStateRules.DeriveConnectionState(item),
                UpdateState: SettingsStateRules.DeriveUpdateState(item, waitingOnSessions),
                WaitingOnSessions: waitingOnSessions,
                Machine: item));
        }

        var specs = ResolveProviders(localAgents);
        var localById = localAgents
            .GroupBy(agent => agent.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var providers = new List<SettingsProvider>(specs.Count);
        foreach (var spec in specs)
        {
            localById.TryGetValue(spec.Id, out var localAgent);
            var perMachine = new List<SettingsProviderMachine>(machines.Count);
            foreach (var machine in machines)
            {
                var isLocal = machine.Machine.Name is null;
                var mirrorState = isLocal ? null : engineData.MirrorsByName.GetValueOrDefault(machine.Key)?.State;
                perMachine.Add(ComposeProviderMachine(
                    spec,
                    machine,
                    isLocal ? localAgent : null,
                    mirrorState,
                    engineData,
                    nowMs));
            }

            // The one on/off decision (ProviderEnablementStore.IsEnabled): a saved setting wins;
            // with none, installed on this machine counts as on — the same rule the reconciler seeds by.
            var localInstalled = perMachine.FirstOrDefault(entry => entry.MachineKey == LocalKey)?.Installed ?? false;
            var enabled = ProviderEnablementStore.IsEnabled(engineData.SavedEnabled, spec.Id, localInstalled);
            IReadOnlyList<SettingsProviderMachine> finalized = enabled
                ? perMachine
                : perMachine.Select(entry => entry with { State = null }).ToArray();
            var providerState = enabled ? SettingsStateRules.DeriveAggregateProviderState(finalized) : null;
            providers.Add(new SettingsProvider(
                Id: spec.Id,
                Name: spec.Name,
                Glyph: ProviderGlyph.Of(spec.VendorFamily ?? spec.Id),
                VendorFamily: spec.VendorFamily,
                Enabled: enabled,
                State: providerState,
                Models: SettingsStateRules.DeriveSharedModels(finalized),
                Machines: finalized));
        }

        var summary = SettingsStateRules.DeriveSummary(providers, machines, reconciling: isReconciling?.Invoke() ?? false);
        return new SettingsState(summary, providers, machines);
    }

    private EngineSnapshotData ReadEngineData(CancellationToken ct)
    {
        using var snapshot = db.OpenSnapshot(ct);

        var providers = snapshot.Query("""
            SELECT machine, provider, account_context, installation, auth_state, reason, facts_json, checked_ms, version
            FROM engine_provider
            WHERE machine = $machine
            """,
            row => new ProviderRow(
                row.Text(0),
                row.Text(1),
                row.Text(2),
                row.Text(3),
                row.Text(4),
                row.IsNull(5) ? null : row.Text(5),
                row.IsNull(6) ? null : row.Text(6),
                row.IsNull(7) ? null : row.Long(7),
                row.Long(8)),
            new SqlArg("$machine", localEngineMachine));

        var signIns = snapshot.Query("""
            SELECT machine, provider, account_context, state, reason, deadline_ms, updated_ms
            FROM engine_sign_in
            WHERE machine = $machine
            ORDER BY updated_ms DESC, id DESC
            """,
            row => new SignInRow(
                row.Text(0),
                row.Text(1),
                row.Text(2),
                row.Text(3),
                row.IsNull(4) ? null : row.Text(4),
                row.Long(5),
                row.Long(6)),
            new SqlArg("$machine", localEngineMachine));

        var mirrors = snapshot.Query("""
            SELECT machine, name, state
            FROM engine_mirror
            WHERE state <> 'retired'
            """,
            row => new MirrorRow(row.Text(0), row.Text(1), row.Text(2)));
        var mirrorsByName = new Dictionary<string, MirrorRow>(StringComparer.Ordinal);
        foreach (var mirror in mirrors)
            mirrorsByName[mirror.Name] = mirror;

        var mirroredRows = snapshot.Query("""
            SELECT r.machine, r.entity_kind, r.row_json
            FROM engine_mirror_row r
            JOIN engine_mirror m ON m.machine = r.machine AND m.generation = r.generation
            WHERE m.state <> 'retired' AND r.entity_kind IN ('provider', 'sign_in')
            """,
            row => (Machine: row.Text(0), Kind: row.Text(1), Json: row.Text(2)));

        foreach (var (machine, kind, json) in mirroredRows)
        {
            if (kind == "provider" && ParseMirroredProvider(machine, json) is { } providerRow)
                providers.Add(providerRow);
            else if (kind == "sign_in" && ParseMirroredSignIn(machine, json) is { } signInRow)
                signIns.Add(signInRow);
        }

        signIns.Sort((a, b) => b.UpdatedMs.CompareTo(a.UpdatedMs));
        var savedEnabled = ProviderEnablementStore.ReadAll(snapshot);
        return new EngineSnapshotData(mirrorsByName, providers, signIns, savedEnabled);
    }

    private SettingsProviderMachine ComposeProviderMachine(
        ProviderSpec spec,
        SettingsMachine machine,
        AgentDescriptor? localAgent,
        string? mirrorState,
        EngineSnapshotData engineData,
        long nowMs)
    {
        var isLocal = machine.Machine.Name is null;
        var expectedAccount = localAgent is null ? null : SafeAccountContext(localAgent);
        var providerRow = FindProviderRow(engineData.Providers, machine.EngineMachine, spec.Id, expectedAccount);
        var accountContext = expectedAccount ?? providerRow?.AccountContext;
        var activeSignIn = FindActiveSignIn(engineData.SignIns, machine.EngineMachine, spec.Id, accountContext, nowMs);
        var failedSignIn = FindLatestFailedSignIn(engineData.SignIns, machine.EngineMachine, spec.Id, accountContext);

        var facts = ParseFacts(providerRow?.FactsJson);
        var updates = machine.Machine.Updates;
        var adapterUpdate = spec.AdapterPackage is null || updates is null
            ? null
            : updates.FirstOrDefault(u => u.Kind == "adapter" && u.Name == spec.AdapterPackage);
        var cliUpdate = spec.VendorFamily is null || updates is null
            ? null
            : updates.FirstOrDefault(u => u.Kind == "cli" && u.Name == spec.VendorFamily);

        var adapterVersion = adapterUpdate?.Current ?? facts?.Version;
        var cliVersion = cliUpdate?.Current;
        var localDescriptorInvalid = localAgent is not null && localAgent.Invalid() is not null;
        var adapterMissingFromUpdates = adapterUpdate is { Current: null, State: UpdateItem.Behind };

        var installed = SettingsStateRules.ResolveInstalled(
            isLocal,
            localAgent,
            localDescriptorInvalid,
            adapterUpdate,
            cliUpdate,
            providerRow,
            adapterMissingFromUpdates);
        var explicitlyNotInstalled = providerRow?.Installation == "not_installed"
            || adapterMissingFromUpdates
            || (isLocal && !installed);

        var signInState = SettingsStateRules.DeriveSignInState(providerRow, activeSignIn);
        var lastFailure = SettingsStateRules.DeriveLastFailure(
            localAgent,
            localDescriptorInvalid,
            providerRow,
            activeSignIn,
            failedSignIn,
            adapterMissingFromUpdates,
            // Raw check output is held in memory by the effects, never in the engine's safe facts;
            // a remote's detail never leaves its machine, so remotes fall back to a plain sentence.
            isLocal && providerRow is not null
                ? failureDetails?.Get($"{spec.Id}/{providerRow.AccountContext}")
                : null);

        // A failed install or update of this provider's adapter or CLI is the provider's own
        // failure (its note is already a fixed safe sentence), not just a machine warning. It
        // stands in for a plain not-installed result, which is usually the same event seen twice.
        var installFailed = machine.UpdateState == MachineUpdateState.Failed
            && machine.Machine.UpdateNote is not null
            && (adapterUpdate is { State: UpdateItem.Behind } || cliUpdate is { State: UpdateItem.Behind });
        if (installFailed
            && (lastFailure is null
                || adapterMissingFromUpdates
                || providerRow?.Installation == "not_installed"
                || providerRow?.Reason == "not_installed"))
            lastFailure = SettingsStateRules.InstallFailure(machine.Machine.UpdateNote);

        // Local evidence must be from a check in this run; a remote's facts count once its mirror is live.
        var checkedThisRun = isLocal
            ? providerRow?.CheckedMs is { } checkedMs && checkedMs >= _runStartMs
            : mirrorState == "live";
        var state = SettingsStateRules.DeriveProviderMachineState(
            machine,
            mirrorState,
            providerRow,
            activeSignIn,
            explicitlyNotInstalled,
            localDescriptorInvalid,
            lastFailure,
            checkedThisRun);

        var providerTargetId = accountContext is null ? null : $"{spec.Id}/{accountContext}";
        return new SettingsProviderMachine(
            MachineKey: machine.Key,
            EngineMachine: machine.EngineMachine,
            State: state,
            Installed: installed,
            AdapterVersion: adapterVersion,
            CliVersion: cliVersion,
            SignInState: signInState,
            Models: facts?.Models,
            LastFailure: lastFailure,
            AccountContext: accountContext,
            ProviderTargetId: providerTargetId,
            CheckedMs: providerRow?.CheckedMs,
            Version: providerRow?.Version ?? 0);
    }

    private static IReadOnlyList<ProviderSpec> ResolveProviders(IReadOnlyList<AgentDescriptor> localAgents)
    {
        var specs = new List<ProviderSpec>(ProviderCatalog.Known.Count);
        var byId = localAgents
            .GroupBy(agent => agent.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var known in ProviderCatalog.Known)
        {
            seen.Add(known.Id);
            if (byId.TryGetValue(known.Id, out var configured))
            {
                specs.Add(known with
                {
                    Name = string.IsNullOrWhiteSpace(configured.Name) ? known.Name : configured.Name,
                    VendorFamily = configured.VendorFamily ?? known.VendorFamily
                });
            }
            else
            {
                specs.Add(known);
            }
        }

        var packageByFamily = ProviderCatalog.Known
            .Where(p => p.VendorFamily is not null)
            .ToDictionary(p => p.VendorFamily!, p => p.AdapterPackage, StringComparer.Ordinal);
        foreach (var agent in localAgents)
        {
            if (string.IsNullOrWhiteSpace(agent.Id) || !seen.Add(agent.Id))
                continue;
            var adapterPackage = agent.VendorFamily is not null
                ? packageByFamily.GetValueOrDefault(agent.VendorFamily)
                : null;
            specs.Add(new ProviderSpec(agent.Id, string.IsNullOrWhiteSpace(agent.Name) ? agent.Id : agent.Name, agent.VendorFamily, adapterPackage));
        }

        return specs;
    }

    private static ProviderRow? FindProviderRow(
        IReadOnlyList<ProviderRow> rows,
        string? engineMachine,
        string providerId,
        string? expectedAccount)
    {
        if (engineMachine is null)
            return null;
        if (expectedAccount is not null)
            return rows.FirstOrDefault(r => r.Machine == engineMachine && r.Provider == providerId && r.AccountContext == expectedAccount);

        ProviderRow? best = null;
        foreach (var row in rows)
        {
            if (row.Machine != engineMachine || row.Provider != providerId)
                continue;
            if (best is null || (row.CheckedMs ?? 0) > (best.CheckedMs ?? 0))
                best = row;
        }
        return best;
    }

    private static SignInRow? FindActiveSignIn(
        IReadOnlyList<SignInRow> rows,
        string? engineMachine,
        string providerId,
        string? accountContext,
        long nowMs)
    {
        if (engineMachine is null)
            return null;
        foreach (var row in rows)
        {
            if (row.Machine != engineMachine || row.Provider != providerId)
                continue;
            if (accountContext is not null && row.AccountContext != accountContext)
                continue;
            if (row.State is "starting" or "waiting_for_person" or "finishing" && row.DeadlineMs > nowMs)
                return row;
        }
        return null;
    }

    private static SignInRow? FindLatestFailedSignIn(
        IReadOnlyList<SignInRow> rows,
        string? engineMachine,
        string providerId,
        string? accountContext)
    {
        if (engineMachine is null)
            return null;
        foreach (var row in rows)
        {
            if (row.Machine != engineMachine || row.Provider != providerId)
                continue;
            if (accountContext is not null && row.AccountContext != accountContext)
                continue;
            if (row.State == "failed" && row.Reason is not null)
                return row;
        }
        return null;
    }

    private static string? SafeAccountContext(AgentDescriptor descriptor)
    {
        try
        {
            return AccountContext.Of(descriptor, Environment.GetEnvironmentVariable);
        }
        catch (CallFailed)
        {
            return null;
        }
    }

    private static ProviderFacts? ParseFacts(string? factsJson)
    {
        if (string.IsNullOrWhiteSpace(factsJson))
            return null;
        try
        {
            return JsonSerializer.Deserialize(factsJson, ProvidersJson.Default.ProviderFacts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ProviderRow? ParseMirroredProvider(string machine, string rowJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rowJson);
            var root = document.RootElement;
            var provider = ReadString(root, "provider");
            var account = ReadString(root, "accountContext");
            var installation = ReadString(root, "installation");
            var authState = ReadString(root, "authState");
            if (provider is null || account is null || installation is null || authState is null)
                return null;
            return new ProviderRow(
                machine,
                provider,
                account,
                installation,
                authState,
                ReadString(root, "reason"),
                ReadString(root, "factsJson"),
                ReadLong(root, "checkedMs"),
                ReadLong(root, "version") ?? 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SignInRow? ParseMirroredSignIn(string machine, string rowJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rowJson);
            var root = document.RootElement;
            var provider = ReadString(root, "provider");
            var account = ReadString(root, "accountContext");
            var state = ReadString(root, "state");
            var deadlineMs = ReadLong(root, "deadlineMs");
            var updatedMs = ReadLong(root, "updatedMs");
            if (provider is null || account is null || state is null || deadlineMs is null || updatedMs is null)
                return null;
            return new SignInRow(
                machine,
                provider,
                account,
                state,
                ReadString(root, "reason"),
                deadlineMs.Value,
                updatedMs.Value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadLong(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    private static string LocalDisplayName() => OperatingSystem.IsMacOS() ? "This Mac" : "This machine";
}
