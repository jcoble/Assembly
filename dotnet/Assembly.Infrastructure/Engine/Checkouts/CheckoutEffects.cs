using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Git;
using Assembly.Engine;
using Assembly.Engine.Effects;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine.Handoff;
using Assembly.Infrastructure.Git;

namespace Assembly.Infrastructure.Engine.Checkouts;

/// <summary>One existing host checkout lease and actual session maintenance fence; no Git work in the DB writer.</summary>
internal sealed class CheckoutEffects(IAppDatabase db, IGit git, CheckoutWrites writes, ISessionMaintenance maintenance, string worktreeRoot) : IEngineEffects
{
    public Task<EffectReceipt> ExecuteAsync(Effect effect, CancellationToken ct) => Run(effect, false, ct);
    public Task<EffectReceipt> ReconcileAsync(Effect effect, CancellationToken ct) => Run(effect, true, ct);

    private async Task<EffectReceipt> Run(Effect effect, bool reconcile, CancellationToken ct)
    {
        var intent = effect.Payload.Deserialize(EngineJson.Default.CheckoutOperation);
        if (intent is null || intent.Request is null || string.IsNullOrWhiteSpace(intent.Folder) ||
            !Guid.TryParseExact(intent.OperationId, "D", out _) || intent.Epoch < 0 || effect.Id != "checkout/" + intent.OperationId || effect.SessionId is null)
            return Unknown("checkout_intent_invalid");
        try
        {
            HandoffGit.NoLinks(intent.Folder);
            if (!Directory.Exists(intent.Folder)) return Refuse(intent, "folder_unavailable");
            var top = await git.RunAsync(intent.Folder, ["rev-parse", "--show-toplevel"], null, null, ct);
            if (top.ExitCode != 0 && intent.Request.CheckoutKind is not ("current" or "folder")) return Refuse(intent, "not_git");
            var root = Path.GetFullPath(top.ExitCode == 0 ? GitText(top.Output) : intent.Folder);
            HandoffGit.NoLinks(root);
            if (intent.Request.CheckoutKind is not ("current" or "folder") && root != Path.TrimEndingDirectorySeparator(Path.GetFullPath(intent.Folder)))
                return Refuse(intent, "checkout_root_changed");
            string? destination = null;
            if (intent.Request.CheckoutKind == "folder")
            {
                destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(intent.Request.Folder!));
                HandoffGit.NoLinks(destination);
                if (!Directory.Exists(destination)) return Refuse(intent, "folder_unavailable");
            }
            if (intent.Request.CheckoutKind is "branch" or "worktree" && intent.Request.Branch is not null)
            {
                var listing = await git.RunAsync(root, ["worktree", "list", "--porcelain", "-z"], null, null, ct);
                if (listing.ExitCode != 0) return Unknown("worktrees_unavailable");
                var matches = WorktreePorcelain.Parse(listing.Output).Where(row => row.Branch == intent.Request.Branch).ToArray();
                if (matches.Length > 1) return Unknown("ambiguous_worktree");
                if (matches.Length == 1) { destination = Path.GetFullPath(matches[0].Path); HandoffGit.NoLinks(destination); }
            }
            return await WithRoots(root, destination, async () =>
            {
                ct.ThrowIfCancellationRequested();
                var attempt = maintenance.TryAcquire(effect.SessionId);
                if (attempt.Lease is null) return Refuse(intent, attempt.Reason ?? "busy");
                await using var lease = attempt.Lease;
                if (!Current(effect, intent)) return Unknown("checkout_owner_changed");
                if (!Quiescent(effect, intent)) return Refuse(intent, "busy");
                var before = top.ExitCode == 0 ? await Inspect(root, ct) : (Head: (string?)null, Branch: (string?)null);
                if (intent.Request.CheckoutKind == "current") return Confirm(intent, intent.Folder, before.Branch);
                if (intent.Request.CheckoutKind == "folder")
                    return await Relocate(effect, intent, lease, destination!, (await Inspect(destination!, ct)).Branch, ct);
                if (intent.Request.CheckoutKind == "worktree" && intent.InitialStart is not null)
                    return await InitialWorktree(effect, intent, root, reconcile, ct);
                var branch = intent.Request.Branch!;
                var valid = await git.RunAsync(root, ["check-ref-format", "--branch", branch], null, null, ct);
                if (valid.ExitCode != 0) return Refuse(intent, "invalid_branch");
                var listed = await git.RunAsync(root, ["worktree", "list", "--porcelain", "-z"], null, null, ct);
                if (listed.ExitCode != 0) return Refuse(intent, "worktrees_unavailable");
                var held = WorktreePorcelain.Parse(listed.Output).Where(item => item.Branch == branch).ToArray();
                if (held.Length > 1) return Unknown("ambiguous_worktree");
                if (held.Length == 1 && Path.GetFullPath(held[0].Path) != Path.GetFullPath(intent.Folder))
                {
                    var target = Path.GetFullPath(held[0].Path);
                    if (target != destination) return Unknown("checkout_target_changed");
                    HandoffGit.NoLinks(target);
                    if (!Directory.Exists(target)) return Refuse(intent, "folder_unavailable");
                    var targetState = await Inspect(target, ct);
                    if (targetState.Branch != branch || intent.Request.ExpectedHead is { } expected && targetState.Head != expected)
                        return Unknown("checkout_target_changed");
                    return await Relocate(effect, intent, lease, target, branch, ct);
                }
                if (intent.Request.CheckoutKind == "worktree" && held.Length == 0) return Refuse(intent, "worktree_unavailable");
                if (reconcile)
                {
                    // No Git replay. Without an accepted exact HEAD a matching branch name alone is not proof.
                    if (before.Branch == branch && intent.Request.ExpectedHead is { } expected && before.Head == expected)
                        return Confirm(intent, intent.Folder, before.Branch);
                    if (before.Branch == intent.Branch && (intent.Request.ExpectedHead is null || before.Head == intent.Request.ExpectedHead))
                        return Refuse(intent, "checkout_not_applied");
                    return Unknown("checkout_outcome_unknown");
                }
                if (intent.Request.ExpectedHead is { } head && before.Head != head) return Refuse(intent, "head_changed");
                if (before.Branch == branch && !intent.Request.CreateBranch) return Confirm(intent, intent.Folder, branch);
                if (!Current(effect, intent) || !Quiescent(effect, intent)) return Refuse(intent, "busy");
                var peers = await AcquirePeers(intent.Folder, effect.SessionId, ct);
                if (peers is null) return Refuse(intent, "checkout_busy");
                try
                {
                    if (!Current(effect, intent) || !Quiescent(effect, intent)) return Refuse(intent, "busy");
                    var args = new List<string> { "switch" };
                    if (intent.Request.CreateBranch) args.Add("-c");
                    args.Add(branch);
                    if (intent.Request.CreateBranch && intent.Request.Base is { } basis) args.Add(basis);
                    var changed = await git.RunAsync(root, args, null, null, ct);
                    var after = await Inspect(root, ct);
                    if (!Current(effect, intent)) return Unknown("checkout_owner_changed");
                    if (changed.ExitCode == 0 && after.Branch == branch) return Confirm(intent, intent.Folder, branch);
                    if (after == before) return Refuse(intent, "checkout_conflict");
                    return Unknown("checkout_outcome_unknown");
                }
                finally { foreach (var peer in peers) await peer.DisposeAsync(); }
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or HandoffRefused or Assembly.Application.Common.Calls.CallFailed)
        { return Unknown("checkout_inspection_failed"); }
    }

    private async Task<EffectReceipt> Relocate(Effect effect, CheckoutOperation intent, ISessionMaintenanceLease lease,
        string folder, string? branch, CancellationToken ct)
    {
        if (!Current(effect, intent) || !Quiescent(effect, intent)) return Refuse(intent, "busy");
        var peers = await AcquirePeers(folder, effect.SessionId!, ct);
        if (peers is null) return Refuse(intent, "checkout_busy");
        try
        {
            if (folder != intent.Folder && intent.ProviderSessionId is { } native &&
                !await lease.ProbeResumeAsync(native, folder, ct)) return Refuse(intent, "unsupported_resume");
            return Current(effect, intent) && Quiescent(effect, intent)
                ? Result(intent, new(folder, branch, intent.ProviderSessionId, null, true, lease.ResumeEvidence))
                : Unknown("checkout_owner_changed");
        }
        finally { foreach (var peer in peers) await peer.DisposeAsync(); }
    }

    private Task<EffectReceipt> WithRoots(string root, string? target, Func<Task<EffectReceipt>> work)
    {
        if (target is null || target == root) return writes.RunAsync(root, work);
        var first = string.CompareOrdinal(root, target) < 0 ? root : target;
        var second = first == root ? target : root;
        return writes.RunAsync(first, () => writes.RunAsync(second, work));
    }

    private async Task<List<ISessionMaintenanceLease>?> AcquirePeers(string folder, string selected, CancellationToken ct)
    {
        var leases = new List<ISessionMaintenanceLease>();
        var accepted = false;
        try
        {
            foreach (var peer in db.Query("""
                SELECT s.id,s.state,s.cancel_requested,s.checkout_state,s.reserved_epoch,
                  EXISTS(SELECT 1 FROM engine_needs_me n WHERE n.session_id=s.id AND n.state='open') OR
                  EXISTS(SELECT 1 FROM engine_tool_call t WHERE t.session_id=s.id AND t.ended_ms IS NULL) OR
                  EXISTS(SELECT 1 FROM engine_message m WHERE m.to_session_id=s.id AND m.in_flight=1) OR
                  EXISTS(SELECT 1 FROM engine_outbox o WHERE o.session_id=s.id AND o.state NOT IN ('done','failed','dead')) OR
                  EXISTS(SELECT 1 FROM engine_step st WHERE st.session_id=s.id AND st.state IN ('running','waiting')) OR
                  EXISTS(SELECT 1 FROM engine_run ru WHERE ru.owner_session_id=s.id AND ru.state NOT IN ('succeeded','failed','cancelled')) OR
                  EXISTS(SELECT 1 FROM engine_session child WHERE child.parent_id=s.id AND child.state NOT IN ('ended','moved'))
                FROM engine_session s WHERE s.folder=$folder AND s.id<>$id AND s.holder='local'
                  AND s.machine=(SELECT machine_id FROM engine_meta WHERE singleton=1) ORDER BY s.id
                """, row => new
            {
                Id = row.Text(0),
                State = row.Text(1),
                Cancel = row.Long(2) != 0,
                Checkout = row.Text(3),
                Reserved = !row.IsNull(4),
                Busy = row.Long(5) != 0
            }, ct, new SqlArg("$folder", folder), new("$id", selected)))
            {
                if (peer.State is not ("idle" or "ended") || peer.Cancel || peer.Checkout == "pending" || peer.Reserved || peer.Busy) return null;
                var reserved = maintenance.TryAcquire(peer.Id);
                if (reserved.Lease is null) return null;
                leases.Add(reserved.Lease);
            }
            accepted = true; return leases;
        }
        finally
        {
            if (!accepted) foreach (var lease in leases) await lease.DisposeAsync();
        }
    }

    private async Task<EffectReceipt> InitialWorktree(Effect effect, CheckoutOperation intent, string root, bool reconcile, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(worktreeRoot)) return Refuse(intent, "worktree_root_unavailable");
        var ownedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreeRoot));
        try { HandoffGit.NoLinks(ownedRoot); }
        catch (HandoffRefused) { return Refuse(intent, "worktree_root_unavailable"); }
        if (!Directory.Exists(ownedRoot)) return Refuse(intent, "worktree_root_unavailable");
        var branch = "assembly-" + Guid.ParseExact(intent.OperationId, "D").ToString("N");
        var repo = Path.GetFileName(root);
        if (string.IsNullOrEmpty(repo) || repo is "." or "..") return Refuse(intent, "worktree_root_unavailable");
        var relative = Path.Combine(repo, branch.Replace('/', '-'));
        var target = Assembly.Infrastructure.Chat.SessionPaths.Resolve(ownedRoot, relative);
        HandoffGit.NoLinks(target);
        var basis = intent.Request.Base;
        if (string.IsNullOrWhiteSpace(basis) || basis.Length > 256 || basis.Any(char.IsControl)) return Refuse(intent, "invalid_base");
        var resolved = await git.RunAsync(root, ["rev-parse", "--verify", "--end-of-options", basis + "^{commit}"], null, null, ct);
        if (resolved.ExitCode != 0) return Refuse(intent, "invalid_base");
        var oid = GitText(resolved.Output);
        if (!HandoffGit.IsOid(oid)) return Refuse(intent, "invalid_base");
        var listed = await git.RunAsync(root, ["worktree", "list", "--porcelain", "-z"], null, null, ct);
        if (listed.ExitCode != 0) return Unknown("worktrees_unavailable");
        var existing = WorktreePorcelain.Parse(listed.Output).Where(row => row.Branch == branch || row.Path == target).ToArray();
        if (reconcile)
        {
            if (existing.Length == 1 && existing[0].Path == target && existing[0].Branch == branch && existing[0].Head == oid && Directory.Exists(target))
                return Confirm(intent, target, branch);
            return existing.Length == 0 && !Path.Exists(target) ? Refuse(intent, "checkout_not_applied") : Unknown("checkout_outcome_unknown");
        }
        if (existing.Length != 0 || Path.Exists(target)) return Refuse(intent, "worktree_collision");
        var parent = Path.GetDirectoryName(target)!;
        HandoffGit.NoLinks(parent); Directory.CreateDirectory(parent); HandoffGit.NoLinks(parent);
        if (!Current(effect, intent) || !Quiescent(effect, intent)) return Refuse(intent, "busy");
        var added = await git.RunAsync(root, ["worktree", "add", "-b", branch, "--", target, oid], null, null, ct);
        HandoffGit.NoLinks(target);
        if (added.ExitCode == 0 && Current(effect, intent) && await Inspect(target, ct) == (oid, branch))
            return Confirm(intent, target, branch);
        return Path.Exists(target) ? Unknown("checkout_outcome_unknown") : Refuse(intent, "worktree_refused");
    }

    private bool Current(Effect effect, CheckoutOperation intent) => db.QueryFirst("""
        SELECT EXISTS(SELECT 1 FROM engine_outbox o JOIN engine_session s ON s.id=o.session_id
          JOIN engine_meta m ON m.machine_id=s.machine AND m.singleton=1
          WHERE o.id=$effect AND o.state='inflight' AND o.attempt=$attempt AND o.payload_json=$payload
            AND s.id=$session AND s.holder='local' AND s.checkout_state='pending'
            AND s.state IN ('starting','idle','ended') AND s.activity_state<>'running' AND s.cancel_requested=0 AND s.reserved_epoch IS NULL AND s.checkout_operation_id=$operation
            AND s.folder=$folder AND s.provider_session_id IS $native AND s.epoch=$epoch AND o.machine=m.machine_id)
        """, r => r.Long(0) != 0, default, new SqlArg("$effect", effect.Id), new("$attempt", effect.Attempt),
        new("$payload", effect.Payload.GetRawText()), new("$session", effect.SessionId), new("$operation", intent.OperationId),
        new("$folder", intent.Folder), new("$native", intent.ProviderSessionId), new("$epoch", intent.Epoch));

    private bool Quiescent(Effect effect, CheckoutOperation intent) => db.QueryFirst("""
        SELECT NOT EXISTS(SELECT 1 FROM engine_needs_me WHERE session_id=$id AND state='open')
          AND NOT EXISTS(SELECT 1 FROM engine_message WHERE to_session_id=$id AND in_flight=1)
          AND NOT EXISTS(SELECT 1 FROM engine_tool_call WHERE session_id=$id AND ended_ms IS NULL)
          AND NOT EXISTS(SELECT 1 FROM engine_step WHERE session_id=$id AND state IN ('running','waiting'))
          AND NOT EXISTS(SELECT 1 FROM engine_run WHERE owner_session_id=$id AND state NOT IN ('succeeded','failed','cancelled'))
          AND NOT EXISTS(SELECT 1 FROM engine_outbox WHERE session_id=$id AND id<>$effect AND state NOT IN ('done','failed','dead'))
          AND NOT EXISTS(SELECT 1 FROM engine_session WHERE parent_id=$id AND state NOT IN ('ended','moved'))
        """, row => row.Long(0) != 0, default, new SqlArg("$id", effect.SessionId), new("$effect", effect.Id), new("$folder", intent.Folder));

    private async Task<(string? Head, string? Branch)> Inspect(string root, CancellationToken ct)
    {
        var head = await git.RunAsync(root, ["rev-parse", "--verify", "HEAD"], null, null, ct);
        var branch = await git.RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], null, null, ct);
        return (head.ExitCode == 0 ? GitText(head.Output) : null, branch.ExitCode == 0 ? GitText(branch.Output) : null);
    }
    private static string GitText(string output) => output.EndsWith('\n') ? output[..^1] : output;
    private static EffectReceipt Confirm(CheckoutOperation intent, string folder, string? branch) => Result(intent, new(folder, branch, intent.ProviderSessionId, null, true));
    private static EffectReceipt Refuse(CheckoutOperation intent, string reason) => Result(intent, new(intent.Folder, intent.Branch, intent.ProviderSessionId, reason, false));
    private static EffectReceipt Result(CheckoutOperation intent, CheckoutEffectData data) => new(EffectDisposition.Confirmed,
        JsonSerializer.SerializeToElement(data, EngineJson.Default.CheckoutEffectData), null);
    private static EffectReceipt Unknown(string reason) => new(EffectDisposition.Unknown,
        JsonSerializer.SerializeToElement((string?)null, EngineJson.Default.String), reason);
}
