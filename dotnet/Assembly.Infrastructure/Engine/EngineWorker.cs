using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Effects;
using Assembly.Application.Features.Engine.NeedsMe;
using Assembly.Application.Features.Engine.Persistence;
using Assembly.Application.Features.Engine.Runs;

namespace Assembly.Infrastructure.Engine;

/// <summary>Owns effect I/O and the engine's sole earliest-deadline wait; all mutations are journaled commands.</summary>
public sealed class EngineWorker(IAppDatabase db, EngineCommands commands, string machine,
    TimeProvider time, IEngineEffects? effects, IEngineRetention retention)
{
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private int _enable;
    private Task<EffectReceipt>? _active;
    private Candidate? _claimed;
    private bool _recovering = true;
    private long? _retentionDeadline;
    private sealed record Candidate(string Id, long Version, string State, long Epoch);

    public void Wake() => _wake.Writer.TryWrite(true);
    public void EffectsAvailable() { Interlocked.Exchange(ref _enable, 1); Wake(); }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_active is { IsCompleted: true })
                {
                    var receipt = await _active;
                    cancellationToken.ThrowIfCancellationRequested();
                    Complete(receipt);
                    _active = null;
                    _claimed = null;
                }
                var pass = db.Writer.Write("engine.worker", s => Pass(s));
                if (pass.Claim is not null)
                {
                    _claimed = pass.Claim;
                    var effect = EffectTransitions.Read(db, pass.Claim.Id, cancellationToken)!;
                    _active = ExecuteAsync(effect, pass.Claim.State == "uncertain", cancellationToken);
                }
                if (pass.More) continue;
                await WaitAsync(pass.Deadline, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            if (_active is not null)
                try { await _active; } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
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
        if (_retentionDeadline <= now) retention.Apply(s, now);
        Candidate? claim = null;
        if (effects is not null && _active is null)
        {
            claim = Candidates(s, $"state IN ('pending','uncertain') AND (next_ms IS NULL OR next_ms<={now}) AND {EffectTransitions.NoEarlierPending}", 1).FirstOrDefault();
            if (claim is not null && !Apply(s, claim, new ClaimEffect()).Applied) claim = null;
        }
        var deadline = s.QueryFirst("""
            SELECT MIN(deadline) FROM (
                SELECT MIN(deadline_ms) deadline FROM engine_needs_me WHERE state='open'
                UNION ALL SELECT MIN(owner_lost_ms+owner_loss_limit_ms) FROM engine_run
                  WHERE state NOT IN ('succeeded','failed','cancelled','cancelling') AND owner_lost_ms IS NOT NULL)
            """, r => r.IsNull(0) ? (long?)null : r.Long(0));
        if (effects is not null && _active is null && claim is null)
            deadline = Earlier(deadline, s.QueryFirst($"SELECT MIN(next_ms) FROM engine_outbox WHERE state IN ('pending','uncertain') AND {EffectTransitions.NoEarlierPending}",
                r => r.IsNull(0) ? (long?)null : r.Long(0)));
        _retentionDeadline = retention.NextDeadlineMs(s, now);
        return (claim, Earlier(deadline, _retentionDeadline), needs.Count == 100 || runs.Count == 100);
    }

    private void Complete(EffectReceipt receipt)
    {
        var candidate = _claimed! with { Version = _claimed!.Version + 1 };
        CommandResult result;
        try
        {
            result = db.Writer.Write("engine.effect.complete", s => Apply(s, candidate, new CompleteEffect(receipt)));
        }
        catch (InvalidOperationException error)
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
    private static List<Candidate> Candidates(IWriteScope s, string predicate, int limit = 100) => s.Query($"""
        SELECT id,version,state,COALESCE((SELECT epoch FROM engine_session WHERE id=engine_outbox.session_id),0)
        FROM engine_outbox WHERE {predicate} ORDER BY event_seq,rowid LIMIT {limit}
        """, r => new Candidate(r.Text(0), r.Long(1), r.Text(2), r.Long(3)));

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
        finally { Wake(); }
    }

    private async Task WaitAsync(long? deadline, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pushed = _wake.Reader.ReadAsync(wait.Token).AsTask();
        if (deadline is null) { await pushed; return; }
        var delay = Math.Max(0, deadline.Value - time.GetUtcNow().ToUnixTimeMilliseconds());
        var elapsed = Task.Delay(TimeSpan.FromMilliseconds(delay), time, wait.Token);
        await Task.WhenAny(pushed, elapsed);
        await wait.CancelAsync();
        try { await Task.WhenAll(pushed, elapsed); } catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
