using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Engine.Hosting;
using Assembly.Engine.Handoffs;
using Assembly.Infrastructure.Chat;

namespace Assembly.Infrastructure.Engine.Handoff;

/// <summary>Validates an actual launch producer's facts; it does not mint native identity or release evidence.</summary>
public static class NativeSessionFileInputs
{
    /// <summary>Reads positive disposal only through the owning journal; the native producer remains separate.</summary>
    public static async Task<NativeSessionCapture> CaptureAndValidateReleasedSourceAsync(
        INativeSessionWitnessSource producer, EngineRuntime owner, HandoffIdentity identity, CancellationToken ct)
    {
        var release = ReadRelease(owner, identity, ct) ?? throw new HandoffRefused("unavailable");
        var capture = await CaptureAndValidateSourceAsync(producer, owner, identity.SourceSessionId, identity.OldEpoch, ct);
        if (release != ReadRelease(owner, identity, ct)) throw new HandoffRefused("unavailable");
        ValidateRelease(capture, release);
        return capture;
    }

    /// <summary>A field cross-check, not authority to manufacture a release receipt or native witness.</summary>
    public static void ValidateRelease(NativeSessionCapture source, HandoffReleaseEffectData? release)
    {
        if (release is not { Identity: { } identity } ||
            source is not { Storage: { } storage, Current: { } current } ||
            identity.SourceMachine != current.MachineId || storage.MachineId != current.MachineId ||
            identity.SourceSessionId != current.SessionId || release.SessionId != current.SessionId ||
            identity.OldEpoch != current.Epoch || release.Epoch != current.Epoch ||
            string.IsNullOrWhiteSpace(release.ProviderSessionId) || release.ProviderSessionId != current.AcpSessionId ||
            string.IsNullOrWhiteSpace(release.AdapterOperationId) || string.IsNullOrWhiteSpace(release.ReleaseEffectId) ||
            release.ReleaseAttempt < 1)
            throw new HandoffRefused("unavailable");
    }

    private static HandoffReleaseEffectData? ReadRelease(EngineRuntime owner, HandoffIdentity identity, CancellationToken ct) =>
        ((IEngineRuntime)owner).Read(scope => owner.Commands.ReadHandoffRelease(scope, identity), ct);

    public static async Task<NativeSessionCapture> CaptureAndValidateSourceAsync(
        INativeSessionWitnessSource producer, EngineRuntime owner, string sessionId, long expectedEpoch,
        CancellationToken ct)
    {
        if (producer is null) throw new HandoffRefused("unavailable");
        if (string.IsNullOrWhiteSpace(sessionId) || expectedEpoch < 0) throw new HandoffRefused("bad_args");
        ct.ThrowIfCancellationRequested();
        var before = Read(owner, sessionId, expectedEpoch, ct);
        var capture = await producer.ReadCurrentAsync(sessionId, expectedEpoch, ct);
        ct.ThrowIfCancellationRequested();
        if (before != Read(owner, sessionId, expectedEpoch, ct)) throw new HandoffRefused("unavailable");
        Validate(capture);
        var current = capture.Current;
        if (current.SessionId != sessionId || current.Epoch != expectedEpoch || current.MachineId != owner.Machine ||
            current.ProviderId != before.Provider || current.AccountContext != before.Account ||
            current.AcpSessionId != before.Acp || current.Folder != before.Folder)
            throw new HandoffRefused("unavailable");
        return capture;
    }

    public static void Validate(NativeSessionCapture capture)
    {
        if (capture is not { Storage: { } storage, Current: { } current } ||
            new[] { storage.MachineId, storage.ProviderId, storage.VendorFamily,
                storage.LaunchBindingId, current.SessionId, current.MachineId, current.ProviderId,
                current.LaunchBindingId, current.AcpSessionId, current.ResumeId,
                current.HistoryMode, current.Compression }.Any(string.IsNullOrWhiteSpace) || current.Epoch < 0 ||
            storage.MachineId != current.MachineId || storage.ProviderId != current.ProviderId ||
            storage.AccountContext != current.AccountContext || storage.LaunchBindingId != current.LaunchBindingId ||
            !AccountContext.IsValid(storage.AccountContext) || !AccountContext.IsValid(current.AccountContext) ||
            !Path.IsPathFullyQualified(current.Folder))
            throw new HandoffRefused("bad_args");
        if (current.HistoryMode != "single_file_jsonl" || current.Compression != "none")
            throw new HandoffRefused("unsupported");
        ResolveProviderPath(storage.ProviderHome, current.SelectedRelativePath);
        if (storage.CodexSqliteHome is { } sqlite)
        {
            if (!Path.IsPathFullyQualified(sqlite)) throw new HandoffRefused("bad_args");
            HandoffGit.NoLinks(sqlite);
        }
    }

    public static string ResolveProviderPath(string root, string relative)
    {
        if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root) || string.IsNullOrEmpty(relative) ||
            Path.IsPathRooted(relative) || relative.Contains('\\') ||
            relative.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new HandoffRefused("bad_args");
        try
        {
            HandoffGit.NoLinks(root);
            return SessionPaths.Resolve(root, relative);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or CallFailed)
        { throw new HandoffRefused("bad_args"); }
    }

    private sealed record Owning(string Provider, string Account, string Acp, string Folder,
        string State, string Holder, long? Reserved, string? Handoff, long? HandoffVersion, string? HandoffState);

    private static Owning Read(EngineRuntime owner, string session, long epoch, CancellationToken ct) =>
        ((IEngineRuntime)owner).Read(scope =>
        {
            var rows = scope.Query("""
            SELECT s.provider,s.account_context,s.provider_session_id,s.folder,s.state,s.holder,s.reserved_epoch,
                h.id,h.version,h.state
            FROM engine_session s LEFT JOIN engine_handoff h ON h.session_id=s.id AND h.state IN ('requested','accepted')
            WHERE s.id=$session AND s.machine=$machine AND s.epoch=$epoch AND s.holder='local'
                AND s.state NOT IN ('foreign','moved','ended','lost') LIMIT 2
            """, row => row.IsNull(2) || row.IsNull(3) ? null : new Owning(row.Text(0), row.Text(1), row.Text(2), row.Text(3),
                row.Text(4), row.Text(5), row.IsNull(6) ? null : row.Long(6), row.IsNull(7) ? null : row.Text(7),
                row.IsNull(8) ? null : row.Long(8), row.IsNull(9) ? null : row.Text(9)),
            new SqlArg("$session", session), new SqlArg("$machine", owner.Machine), new SqlArg("$epoch", epoch));
            return rows.Count == 1 && rows[0] is { } current ? current : throw new HandoffRefused("unavailable");
        }, ct);
}
