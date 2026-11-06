using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Engine.Handoff;

/// <summary>Resolves only the accepted launch's account and verifies the selected native file's own identity.</summary>
public sealed class NativeSessionProducer(IAppDatabase db, SessionStore sessions) : INativeSessionResolver, INativeSessionWitnessSource
{
    public NativeSessionCapture Resolve(NativeSessionLaunch launch, string session, string? movingTo = null)
    {
        var owner = ReadOwner(launch, session);
        return ResolveFile(launch, owner.Machine, session, owner.Epoch, movingTo);
    }

    private sealed record Owner(string Machine, long Epoch);

    private Owner ReadOwner(NativeSessionLaunch launch, string session)
    {
        using var scope = db.OpenSnapshot(default);
        var owner = scope.QueryFirst("""
            SELECT s.machine,s.epoch,s.provider,s.account_context,s.provider_session_id,s.folder
            FROM engine_session s JOIN engine_meta m ON m.machine_id=s.machine AND m.singleton=1
            WHERE s.id=$id AND s.holder='local' AND s.state NOT IN ('foreign','moved','lost')
            """, row => new
        {
            Machine = row.Text(0),
            Epoch = row.Long(1),
            Provider = row.Text(2),
            Account = row.Text(3),
            Acp = row.IsNull(4) ? null : row.Text(4),
            Folder = row.Text(5)
        }, new SqlArg("$id", session));
        if (owner is null || owner.Provider != launch.ProviderId || owner.Account != launch.AccountContext ||
            owner.Acp != launch.AcpSessionId || owner.Folder != launch.Folder || sessions.NativeLaunch(scope, session) != launch)
            throw new HandoffRefused("native_witness_mismatch");
        return new(owner.Machine, owner.Epoch);
    }

    internal static NativeSessionCapture ResolveFile(NativeSessionLaunch launch, string machine, string session, long epoch, string? movingTo = null)
    {
        if (!Guid.TryParseExact(launch.AcpSessionId, "D", out _) || !Path.IsPathFullyQualified(launch.Folder))
            throw new HandoffRefused("native_witness_mismatch");
        var reader = Reader(launch.VendorFamily);
        var storage = new ProviderFileContext(machine, launch.ProviderId, launch.VendorFamily, launch.AccountContext,
            launch.LaunchBindingId, launch.ProviderHome, reader.SqliteHome(launch.ProviderHome));
        var selected = reader.Select(launch);
        var witness = new NativeSessionWitness(session, epoch, machine, launch.ProviderId, launch.AccountContext,
            launch.LaunchBindingId, launch.AcpSessionId, launch.AcpSessionId, launch.Folder, selected.RelativePath, selected.RolloutId,
            "single_file_jsonl", "none");
        var capture = new NativeSessionCapture(storage, witness);
        NativeSessionFileInputs.Validate(capture);
        _ = Located(reader, capture, movingTo);
        return capture;
    }

    public Task<NativeSessionCapture> ReadCurrentAsync(string sessionId, long expectedEpoch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var launch = sessions.NativeLaunch(sessionId) ?? throw new HandoffRefused("native_witness_required");
        var capture = Resolve(launch, sessionId);
        if (capture.Current.Epoch != expectedEpoch) throw new HandoffRefused("native_witness_mismatch");
        db.Writer.Write("sessions.native.capture", scope =>
        {
            ct.ThrowIfCancellationRequested();
            var actual = scope.QueryFirst("SELECT epoch,folder,provider_session_id FROM engine_session WHERE id=$id AND machine=$machine AND holder='local'",
                row => row.Long(0) == expectedEpoch && row.Text(1) == capture.Current.Folder && row.Text(2) == launch.AcpSessionId,
                new SqlArg("$id", sessionId), new("$machine", capture.Current.MachineId));
            if (!actual) throw new HandoffRefused("native_witness_mismatch");
            sessions.RetainNativeWitness(scope, launch, capture);
            return 0;
        });
        return Task.FromResult(capture);
    }

    public async Task<bool> PrepareFolderAsync(NativeSessionCapture capture, string folder, CancellationToken ct)
    {
        NativeSessionFileInputs.Validate(capture);
        HandoffGit.NoLinks(folder);
        if (!Directory.Exists(folder)) throw new HandoffRefused("no_folder");
        if (folder == capture.Current.Folder) return false;
        var reader = Reader(capture.Storage.VendorFamily);
        var (file, mapped) = Located(reader, capture, folder);
        if (mapped) return true;
        var source = NativeSessionFileInputs.ResolveProviderPath(capture.Storage.ProviderHome, file.RelativePath);
        var target = reader.TargetPath(capture.Storage, folder, capture, file);
        // Both providers' native resume readers need the mapped cwd. Codex keeps its exact rollout path.
        var temporary = target + ".assembly-" + Guid.NewGuid().ToString("N");
        try
        {
            var parent = Path.GetDirectoryName(target)!;
            HandoffGit.NoLinks(parent); Directory.CreateDirectory(parent); HandoffGit.NoLinks(parent);
            await MapFileAsync(reader, source, temporary, capture.Current.Folder, folder, ct);
            HandoffGit.NoLinks(target);
            if (target != source && File.Exists(target))
            {
                // Only an older copy of this conversation, left by an earlier move, may be brought up to date.
                if (!await StartsWithAsync(temporary, target, ct)) throw new HandoffRefused("native_file_conflict");
                File.Move(temporary, target, overwrite: true);
                return false;
            }
            File.Move(temporary, target, overwrite: target == source);
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task UndoFolderAsync(NativeSessionCapture capture, string folder, CancellationToken ct)
    {
        var reader = Reader(capture.Storage.VendorFamily);
        var current = capture.Current;
        var file = new NativeFile(current.SelectedRelativePath, current.ResumeId, current.RolloutId);
        var source = NativeSessionFileInputs.ResolveProviderPath(capture.Storage.ProviderHome, file.RelativePath);
        var target = reader.TargetPath(capture.Storage, folder, capture, file);
        if (target != source)
        {
            File.Delete(target);
            return;
        }
        var temporary = target + ".assembly-" + Guid.NewGuid().ToString("N");
        try
        {
            await MapFileAsync(reader, source, temporary, folder, current.Folder, ct);
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>The file, and whether an interrupted move already rewrote it in place for <paramref name="movingTo"/>.</summary>
    private static (NativeFile File, bool Mapped) Located(IProviderSessionFile reader, NativeSessionCapture capture, string? movingTo)
    {
        try { return (reader.Locate(capture.Storage, capture.Current) ?? throw new HandoffRefused("missing"), false); }
        catch (HandoffRefused error) when (error.Message != "missing" && movingTo is not null && movingTo != capture.Current.Folder)
        {
            try
            {
                var file = reader.Locate(capture.Storage, capture.Current with { Folder = movingTo });
                if (file is not null) return (file, true);
            }
            catch (HandoffRefused) { /* The original refusal is the answer. */ }
            throw;
        }
    }

    private static async Task MapFileAsync(IProviderSessionFile reader, string source, string destination,
        string from, string to, CancellationToken ct)
    {
        using var input = new StreamReader(source, new UTF8Encoding(false, true));
        await using var output = new StreamWriter(new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false));
        while (await input.ReadLineAsync(ct) is { } line)
            await output.WriteLineAsync(reader.MapLine(line, from, to).AsMemory(), ct);
    }

    private static async Task<bool> StartsWithAsync(string whole, string start, CancellationToken ct)
    {
        await using var a = File.OpenRead(whole);
        await using var b = File.OpenRead(start);
        if (b.Length > a.Length) return false;
        var left = new byte[81920];
        var right = new byte[left.Length];
        while (await b.ReadAsync(right, ct) is var read and > 0)
        {
            await a.ReadExactlyAsync(left.AsMemory(0, read), ct);
            if (!left.AsSpan(0, read).SequenceEqual(right.AsSpan(0, read))) return false;
        }
        return true;
    }

    private static readonly IProviderSessionFile[] Readers = [new ClaudeSessionFile(), new CodexSessionFile()];

    private static IProviderSessionFile Reader(string family) =>
        Readers.FirstOrDefault(reader => reader.Provider == family) ?? throw new HandoffRefused("unsupported");
}
