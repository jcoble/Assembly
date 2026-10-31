using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Persistence;
using Assembly.Application.Features.Engine.Runs;

namespace Assembly.Application.Features.Engine.Prs;

public static class PrTransitions
{
    private sealed record Pr(long Version, string RunId, string State, string Head, long? ObservedMs);
    private sealed record Run(long Version, string Owner, long Epoch, string? TaskUrl, string? PrId, string State);

    public static CommandResult Apply(IWriteScope s, CommandContext c, PrCommand command)
    {
        if (string.IsNullOrWhiteSpace(c.EntityId)) return Refused(c, "bad_args");
        var pr = Find(s, c.EntityId);
        var runId = command is ReportPr report ? report.RunId : pr?.RunId;
        var run = s.QueryFirst("""
            SELECT r.version,r.owner_session_id,se.epoch,r.task_url,r.pr_id,r.state
            FROM engine_run r JOIN engine_session se ON se.id=r.owner_session_id WHERE r.id=$id
            """, r => new Run(r.Long(0), r.Text(1), r.Long(2), r.IsNull(3) ? null : r.Text(3),
                r.IsNull(4) ? null : r.Text(4), r.Text(5)), new SqlArg("$id", runId));
        if (run is null) return Refused(c, "bad_args", pr);
        if (command is ObservePr && c.Actor.Kind != ActorKind.System ||
            command is ReportPr && !RunTransitions.CanControl(c, run.Owner))
            return Refused(c, "forbidden", pr, run.Epoch);
        if (c.Epoch != run.Epoch) return Refused(c, "stale_epoch", pr, run.Epoch);
        // Older trusted observations are harmless even if their expected version is obsolete.
        if (command is ObservePr observation && pr?.ObservedMs is long observed && observation.ObservedMs < observed)
            return new(false, false, null, pr.Version, 0, run.Epoch);
        if (c.ExpectedVersion != (pr?.Version ?? 0)) return Refused(c, "version_conflict", pr, run.Epoch);
        return command switch
        {
            ReportPr claim => Report(s, c, claim, pr, run),
            ObservePr trusted when pr is not null => Observe(s, c, trusted, pr, run),
            _ => Refused(c, "bad_args", pr, run.Epoch)
        };
    }

    private static CommandResult Report(IWriteScope s, CommandContext c, ReportPr command, Pr? pr, Run run)
    {
        if (command.Number <= 0 || string.IsNullOrWhiteSpace(command.Repo) || string.IsNullOrWhiteSpace(command.Head) ||
            !Uri.TryCreate(command.Url, UriKind.Absolute, out var url) || url.Scheme != "https" ||
            pr is not null && pr.RunId != command.RunId || run.PrId is not null && run.PrId != c.EntityId ||
            s.QueryFirst("SELECT COUNT(*) FROM engine_pr WHERE repo=$repo AND number=$number AND id!=$id",
                r => r.Long(0), new("$repo", command.Repo), new("$number", command.Number), new("$id", c.EntityId)) > 0)
            return Refused(c, "bad_args", pr, run.Epoch);
        if (pr is null)
            s.Execute("""
                INSERT INTO engine_pr(id,version,machine,created_ms,updated_ms,task_url,run_id,repo,number,head,state,reported_by,reported_state,url)
                VALUES($id,1,$machine,$now,$now,$task,$run,$repo,$number,$head,'not_created',$by,$claim,$url)
                """, new("$id", c.EntityId), new("$machine", c.Machine), new("$now", c.NowMs), new("$task", run.TaskUrl),
                new("$run", command.RunId), new("$repo", command.Repo), new("$number", command.Number), new("$head", command.Head),
                new("$by", c.Actor.Id), new("$claim", command.ReportedState), new("$url", command.Url));
        else
            s.Execute("""
                UPDATE engine_pr SET version=version+1,updated_ms=$now,repo=$repo,number=$number,head=$head,
                    reported_by=$by,reported_state=$claim,url=$url WHERE id=$id
                """, new("$id", c.EntityId), new("$now", c.NowMs), new("$repo", command.Repo), new("$number", command.Number),
                new("$head", command.Head), new("$by", c.Actor.Id), new("$claim", command.ReportedState), new("$url", command.Url));
        var version = (pr?.Version ?? 0) + 1;
        List<EventDraft> events = [Changed(c, version, command.RunId, pr?.State ?? "not_created", run.Epoch)];
        if (run.PrId is null)
        {
            s.Execute("UPDATE engine_run SET pr_id=$pr,version=version+1,updated_ms=$now WHERE id=$id",
                new("$pr", c.EntityId), new("$now", c.NowMs), new("$id", command.RunId));
            events.Add(new("run.changed", "run", command.RunId, run.Version + 1, false,
                new(run.Owner, command.RunId, null, null, run.State, run.Epoch)));
        }
        return EngineJournal.Commit(s, c, version, run.Epoch, events, []);
    }

    private static CommandResult Observe(IWriteScope s, CommandContext c, ObservePr command, Pr pr, Run run)
    {
        if (command.Head != pr.Head) return Refused(c, "head_mismatch", pr, run.Epoch);
        if (pr.State is "merged" or "closed" || command.State is not ("open" or "merged" or "closed" or "unknown"))
            return Refused(c, "invalid_transition", pr, run.Epoch);
        s.Execute("""
            UPDATE engine_pr SET version=version+1,updated_ms=$now,state=$state,observed_head=$head,observed_ms=$observed WHERE id=$id
            """, new("$now", c.NowMs), new("$state", command.State), new("$head", command.Head),
            new("$observed", command.ObservedMs), new("$id", c.EntityId));
        return EngineJournal.Commit(s, c, pr.Version + 1, run.Epoch,
            [Changed(c, pr.Version + 1, pr.RunId, command.State, run.Epoch)], []);
    }

    private static Pr? Find(IWriteScope s, string id) => s.QueryFirst("""
        SELECT version,run_id,state,head,observed_ms FROM engine_pr WHERE id=$id
        """, r => new Pr(r.Long(0), r.Text(1), r.Text(2), r.Text(3), r.IsNull(4) ? null : r.Long(4)), new SqlArg("$id", id));

    private static EventDraft Changed(CommandContext c, long version, string runId, string state, long epoch) =>
        new("pr.changed", "pr", c.EntityId, version, false, new(null, runId, null, null, state, epoch));

    private static CommandResult Refused(CommandContext c, string error, Pr? pr = null, long? epoch = null) =>
        new(false, false, error, pr?.Version ?? 0, 0, epoch ?? c.Epoch);
}
