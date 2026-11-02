using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;

namespace Assembly.Engine.Execution;

/// <summary>Owns effect I/O and the engine's sole earliest-deadline wait; all mutations are journaled commands.</summary>
public sealed class EngineWorker(IAppDatabase db, EngineCommands commands, string machine,
    TimeProvider time, IEngineEffects? effects, IEngineRetention retention)
{
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private int _enable;
    private readonly Dictionary<string, ActiveEffect> _active = [];
    private sealed record ActiveEffect(Candidate Claim, Task<EffectReceipt> Task, CancellationTokenSource Stop);
    private bool _recovering = true;
    private string? _releaseAfter = "";
    private long? _retentionDeadline;
    private sealed record Candidate(string Id, long Version, string State, long Epoch, string? Owner = null);

    public void Wake() => _wake.Writer.TryWrite(true);
    public void EffectsAvailable() { Interlocked.Exchange(ref _enable, 1); Wake(); }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var (owner, active) in _active.Where(pair => pair.Value.Task.IsCompleted).ToArray())
                {
                    var receipt = await active.Task;
                    cancellationToken.ThrowIfCancellationRequested();
                    Complete(active.Claim, receipt);
                    active.Stop.Dispose();
                    _active.Remove(owner);
                }
                var pass = db.Writer.Write("engine.worker", s => Pass(s));
                if (pass.Claim is { } claim)
                {
                    var effect = EffectTransitions.Read(db, claim.Id, cancellationToken)!;
                    var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _active.Add(claim.Owner!, new(claim, ExecuteAsync(effect, claim.State == "uncertain", stop.Token), stop));
                }
                if (pass.More || pass.Claim is not null) continue;
                await WaitAsync(pass.Deadline, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            foreach (var active in _active.Values) await active.Stop.CancelAsync();
            foreach (var active in _active.Values)
            {
                try { await active.Task; }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                finally { active.Stop.Dispose(); }
            }
            _active.Clear();
        }
    }

    private (Candidate? Claim, long? Deadline, bool More) Pass(IWriteScope s)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (_recovering)
        {
            var rows = Candidates(s, "state='inflight'");
            foreach (var row in rows) Apply(s, row, new RecoverEffect());
            _recovering = rows.Count == 100;
            if (_recovering) return (null, null, true);
        }
        if (_releaseAfter is not null)
        {
            var recipients = s.Query("""
                SELECT DISTINCT m.to_session_id,se.epoch FROM engine_message m JOIN engine_session se ON se.id=m.to_session_id
                WHERE m.state='queued' AND m.in_flight=0 AND m.to_session_id>$after
                ORDER BY m.to_session_id LIMIT 100
                """, r => (Id: r.Text(0), Epoch: r.Long(1)), new SqlArg("$after", _releaseAfter));
            foreach (var recipient in recipients)
            {
                var context = Context(new(recipient.Id, 0, "", recipient.Epoch));
                List<EffectDraft> drafts = [];
                var events = MessageRelease.Next(s, context, recipient.Id, drafts);
                EngineJournal.Commit(s, context, 0, recipient.Epoch, events, drafts);
                _releaseAfter = recipient.Id;
            }
            if (recipients.Count == 100) return (null, null, true);
            _releaseAfter = null;
        }
        if (Volatile.Read(ref _enable) != 0)
        {
            var rows = Candidates(s, "state='unavailable'");
            foreach (var row in rows) Apply(s, row, new EnableEffect());
            if (rows.Count < 100) Interlocked.Exchange(ref _enable, 0);
            else return (null, null, true);
        }
        var needs = s.Query("""
            SELECT n.id,n.version,se.epoch FROM engine_needs_me n JOIN engine_session se ON se.id=n.session_id
            WHERE n.state='open' AND n.deadline_ms<=$now ORDER BY n.deadline_ms LIMIT 100
            """, r => new Candidate(r.Text(0), r.Long(1), "", r.Long(2)), new SqlArg("$now", now));
        foreach (var need in needs) Apply(s, need, new ResolveNeed("expired", null));
        var runs = s.Query("""
            SELECT r.id,r.version,se.epoch FROM engine_run r JOIN engine_session se ON se.id=r.owner_session_id
            WHERE r.state NOT IN ('succeeded','failed','cancelled','cancelling') AND se.state='lost'
                AND r.owner_lost_ms+r.owner_loss_limit_ms<=$now LIMIT 100
            """, r => new Candidate(r.Text(0), r.Long(1), "", r.Long(2)), new SqlArg("$now", now));
        foreach (var run in runs)
        {
            var context = Context(run);
            EngineJournal.Execute(s, context, "expire_lost_owner", () => RunTransitions.ExpireLostOwner(s, context, run.Id));
        }
        Candidate? claim = null;
        if (effects is not null)
        {
            var available = AvailableOwners();
            claim = Candidates(s, $"state IN ('pending','uncertain') AND (next_ms IS NULL OR next_ms<={now}) AND {EffectTransitions.NoEarlierUnresolved} {available.Sql}", 1, available.Args).FirstOrDefault();
            if (claim is not null && !Apply(s, claim, new ClaimEffect()).Applied) claim = null;
        }
        var deadline = s.QueryFirst("""
            SELECT MIN(deadline) FROM (
                SELECT MIN(deadline_ms) deadline FROM engine_needs_me WHERE state='open'
                UNION ALL SELECT MIN(owner_lost_ms+owner_loss_limit_ms) FROM engine_run
                  WHERE state NOT IN ('succeeded','failed','cancelled','cancelling') AND owner_lost_ms IS NOT NULL)
            """, r => r.IsNull(0) ? (long?)null : r.Long(0));
        if (effects is not null && claim is null)
        {
            var available = AvailableOwners();
            deadline = Earlier(deadline, s.QueryFirst($"SELECT MIN(next_ms) FROM engine_outbox WHERE state IN ('pending','uncertain') AND {EffectTransitions.NoEarlierUnresolved} {available.Sql}",
                r => r.IsNull(0) ? (long?)null : r.Long(0), available.Args));
        }
        retention.Apply(s, now);
        _retentionDeadline = retention.NextDeadlineMs(s, now);
        return (claim, Earlier(deadline, _retentionDeadline), needs.Count == 100 || runs.Count == 100);
    }

    private void Complete(Candidate claimed, EffectReceipt receipt)
    {
        var candidate = claimed with { Version = claimed.Version + 1 };
        CommandResult result;
        try
        {
            result = db.Writer.Write("engine.effect.complete", s => Apply(s, candidate, new CompleteEffect(receipt)));
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            // The transaction rolled back: retain uncertainty rather than replaying external success.
            result = new(false, false, error.Message, candidate.Version, 0, candidate.Epoch);
        }
        if (!result.Applied)
            db.Writer.Write("engine.effect.uncertain", s => Apply(s, candidate,
                new CompleteEffect(new(EffectDisposition.Unknown, JsonSerializer.SerializeToElement((string?)null, EngineJson.Default.String), result.Error))));
    }

    private static long? Earlier(long? a, long? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
    private CommandContext Context(Candidate row) => new(Guid.NewGuid(), machine, new(ActorKind.System, "engine.worker"),
        row.Id, row.Version, row.Epoch, time.GetUtcNow().ToUnixTimeMilliseconds());
    private CommandResult Apply(IWriteScope s, Candidate row, EngineCommand command) => commands.Apply(s, Context(row), command);
    private static List<Candidate> Candidates(IWriteScope s, string predicate, int limit = 100, params SqlArg[] args) => s.Query($"""
        SELECT id,version,state,COALESCE((SELECT epoch FROM engine_session WHERE id=engine_outbox.session_id),0),COALESCE(session_id,'effect/'||id)
        FROM engine_outbox WHERE {predicate} ORDER BY event_seq,rowid LIMIT {limit}
        """, r => new Candidate(r.Text(0), r.Long(1), r.Text(2), r.Long(3), r.Text(4)), args);

    private (string Sql, SqlArg[] Args) AvailableOwners()
    {
        var args = _active.Keys.Select((owner, i) => new SqlArg("$active" + i, owner)).ToArray();
        return args.Length == 0 ? ("", args) :
            ($"AND COALESCE(session_id,'effect/'||id) NOT IN ({string.Join(',', args.Select(arg => arg.Name))})", args);
    }

    private async Task<EffectReceipt> ExecuteAsync(Effect effect, bool reconcile, CancellationToken cancellationToken)
    {
        try
        {
            return reconcile ? await effects!.ReconcileAsync(effect, cancellationToken) : await effects!.ExecuteAsync(effect, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            // An exception does not prove whether external I/O reached its destination.
            return new(EffectDisposition.Unknown, JsonSerializer.SerializeToElement((string?)null, EngineJson.Default.String), error.Message);
        }
    }

    private async Task WaitAsync(long? deadline, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pushed = _wake.Reader.ReadAsync(wait.Token).AsTask();
        Task? elapsed = deadline is null ? null : Task.Delay(
            TimeSpan.FromMilliseconds(Math.Max(0, deadline.Value - time.GetUtcNow().ToUnixTimeMilliseconds())), time, wait.Token);
        var signals = _active.Values.Select(active => (Task)active.Task).Append(pushed);
        if (elapsed is not null) signals = signals.Append(elapsed);
        await Task.WhenAny(signals);
        await wait.CancelAsync();
        try { if (elapsed is null) await pushed; else await Task.WhenAll(pushed, elapsed); } catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
