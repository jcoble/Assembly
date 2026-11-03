using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;

namespace Assembly.Engine.Prs;

public static class PrTransitions
{
    private sealed record Pr(long Version, string? RunId, string? OwnerSessionId, string State, string Head, long? ObservedMs, string Repo, long Number, string? ReportedBy, string? ReportedState, string? Url);
    private sealed record Run(long Version, string Owner, long Epoch, string? TaskUrl, string? PrId, string State);

    public static CommandResult Apply(IWriteScope s, CommandContext c, PrCommand command)
    {
        if (command is RecordPrObservation or ApplyPrObservation or PrObservationRecorded)
            return PrObservationTransitions.Apply(s, c, command);
        if (string.IsNullOrWhiteSpace(c.EntityId)) return Refused(c, "bad_args");
        var pr = Find(s, c.EntityId);
        var runId = command is ReportPr report ? report.RunId : pr?.RunId;
        var ownerId = command is ReportPr ownerReport ? ownerReport.OwnerSessionId : pr?.OwnerSessionId;
        var run = runId is not null ? s.QueryFirst("""
            SELECT r.version,r.owner_session_id,se.epoch,r.task_url,r.pr_id,r.state
            FROM engine_run r JOIN engine_session se ON se.id=r.owner_session_id WHERE r.id=$id
            """, r => new Run(r.Long(0), r.Text(1), r.Long(2), r.IsNull(3) ? null : r.Text(3),
                r.IsNull(4) ? null : r.Text(4), r.Text(5)), new SqlArg("$id", runId)) : s.QueryFirst("""
            SELECT version,id,epoch,task_url,state FROM engine_session WHERE id=$id
            """, r => new Run(r.Long(0), r.Text(1), r.Long(2), r.IsNull(3) ? null : r.Text(3), null, r.Text(4)),
            new SqlArg("$id", ownerId));
        if (run is null || runId is not null && ownerId is not null && ownerId != run.Owner)
            return Refused(c, "bad_args", pr);
        var repeat = command is ReportPr repeated && pr is not null && c.Actor.Kind == ActorKind.Session &&
            pr.RunId == repeated.RunId && pr.OwnerSessionId == repeated.OwnerSessionId && pr.Repo == repeated.Repo &&
            pr.Number == repeated.Number && pr.Head == repeated.Head && pr.Url == repeated.Url &&
            pr.ReportedState == repeated.ReportedState && pr.ReportedBy == c.Actor.Id && (runId is null ? CanReportOwner(s, c, run) : CanRepeat(s, c, runId, run));
        if (command is ObservePr && c.Actor.Kind != ActorKind.System ||
            command is ReportPr && !repeat && !(runId is null ? CanReportOwner(s, c, run) : CanReport(s, c, runId, run)))
            return Refused(c, "forbidden", pr, run.Epoch);
        if (c.Epoch != run.Epoch) return Refused(c, "stale_epoch", pr, run.Epoch);
        // Older trusted observations are harmless even if their expected version is obsolete.
        if (command is ObservePr observation && pr?.ObservedMs is long observed && observation.ObservedMs < observed)
            return new(false, false, null, pr.Version, 0, run.Epoch);
        if (c.ExpectedVersion != (pr?.Version ?? 0)) return Refused(c, "version_conflict", pr, run.Epoch);
        if (repeat) return new(true, false, null, pr!.Version, 0, run.Epoch);
        if (command is ReportPr && c.Actor.Kind == ActorKind.Session &&
            runId is not null && (RunTransitions.IsTerminal(run.State) || run.State == "cancelling"))
            return Refused(c, run.State == "cancelling" ? "run_cancelling" : "invalid_transition", pr, run.Epoch);
        return command switch
        {
            ReportPr claim => Report(s, c, claim, pr, run),
            ObservePr trusted when pr is not null => Observe(s, c, trusted, pr, run),
            _ => Refused(c, "bad_args", pr, run.Epoch)
        };
    }

    private static CommandResult Report(IWriteScope s, CommandContext c, ReportPr command, Pr? pr, Run run)
    {
        if (pr is not null && pr.State is ("merged" or "closed") &&
            (command.Repo != pr.Repo || command.Number != pr.Number || command.Head != pr.Head))
            return Refused(c, "invalid_transition", pr, run.Epoch);
        if (command.Number <= 0 || string.IsNullOrWhiteSpace(command.Repo) || string.IsNullOrWhiteSpace(command.Head) ||
            !Uri.TryCreate(command.Url, UriKind.Absolute, out var url) || url.Scheme != "https" ||
            pr is not null && (pr.RunId != command.RunId || pr.OwnerSessionId != command.OwnerSessionId) ||
            s.QueryFirst("SELECT COUNT(*) FROM engine_pr WHERE repo=$repo AND number=$number AND id!=$id",
                r => r.Long(0), new("$repo", command.Repo), new("$number", command.Number), new("$id", c.EntityId)) > 0)
            return Refused(c, "bad_args", pr, run.Epoch);
        if (pr is null)
            s.Execute("""
                INSERT INTO engine_pr(id,version,machine,created_ms,updated_ms,task_url,run_id,owner_session_id,repo,number,head,state,reported_by,reported_state,url)
                VALUES($id,1,$machine,$now,$now,$task,$run,$owner,$repo,$number,$head,'not_created',$by,$claim,$url)
                """, new("$id", c.EntityId), new("$machine", c.Machine), new("$now", c.NowMs), new("$task", run.TaskUrl),
                new("$run", command.RunId), new("$owner", command.OwnerSessionId), new("$repo", command.Repo), new("$number", command.Number), new("$head", command.Head),
                new("$by", c.Actor.Id), new("$claim", command.ReportedState), new("$url", command.Url));
        else
            s.Execute("""
                UPDATE engine_pr SET version=version+1,updated_ms=$now,repo=$repo,number=$number,head=$head,
                    reported_by=$by,reported_state=$claim,url=$url WHERE id=$id
                """, new("$id", c.EntityId), new("$now", c.NowMs), new("$repo", command.Repo), new("$number", command.Number),
                new("$head", command.Head), new("$by", c.Actor.Id), new("$claim", command.ReportedState), new("$url", command.Url));
        var version = (pr?.Version ?? 0) + 1;
        List<EventDraft> events = [Changed(c, version, command.RunId, pr?.State ?? "not_created", run.Epoch, command.OwnerSessionId)];
        if (command.RunId is not null && run.PrId is null)
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
        if (string.IsNullOrWhiteSpace(command.Head) || command.ObservedMs < 0) return Refused(c, "bad_args", pr, run.Epoch);
        if (pr.ObservedMs is long previous && command.ObservedMs <= previous)
            return new(false, false, null, pr.Version, 0, run.Epoch);
        if (pr.State is "merged" or "closed" || command.State is not ("open" or "merged" or "closed" or "unknown"))
            return Refused(c, "invalid_transition", pr, run.Epoch);
        s.Execute("""
            UPDATE engine_pr SET version=version+1,updated_ms=$now,state=$state,observed_head=$head,observed_ms=$observed WHERE id=$id
            """, new("$now", c.NowMs), new("$state", command.State), new("$head", command.Head),
            new("$observed", command.ObservedMs), new("$id", c.EntityId));
        return EngineJournal.Commit(s, c, pr.Version + 1, run.Epoch,
            [Changed(c, pr.Version + 1, pr.RunId, command.State, run.Epoch, pr.OwnerSessionId)], []);
    }

    private static bool CanReportOwner(IWriteScope s, CommandContext c, Run owner) =>
        c.Actor.Kind == ActorKind.Window || c.Actor.Kind == ActorKind.Session && c.Actor.Id == owner.Owner &&
        owner.State is not ("ended" or "lost" or "handing_off");

    private static bool CanReport(IWriteScope s, CommandContext c, string runId, Run run)
    {
        if (c.Actor.Kind == ActorKind.Window) return true;
        return c.Actor.Kind == ActorKind.Session && s.QueryFirst("""
            SELECT COUNT(*) FROM engine_session se WHERE se.id=$actor AND se.state NOT IN ('ended','lost','handing_off')
             AND (se.id=$owner OR (se.parent_id=$owner AND se.run_id=$run AND EXISTS(
                SELECT 1 FROM engine_step st WHERE st.session_id=se.id AND st.run_id=$run AND st.state IN ('running','waiting'))))
            """, r => r.Long(0), new("$actor", c.Actor.Id), new("$owner", run.Owner), new("$run", runId)) == 1;
    }

    private static bool CanRepeat(IWriteScope s, CommandContext c, string runId, Run run) => s.QueryFirst("""
        SELECT COUNT(*) FROM engine_session se WHERE se.id=$actor AND se.parent_id=$owner AND se.run_id=$run
         AND se.state NOT IN ('ended','lost','handing_off')
         AND (SELECT COUNT(*) FROM engine_step st WHERE st.session_id=se.id AND st.run_id=$run)=1
         AND EXISTS(SELECT 1 FROM engine_step st WHERE st.session_id=se.id AND st.run_id=$run AND st.state IN ('succeeded','failed'))
        """, r => r.Long(0), new("$actor", c.Actor.Id), new("$owner", run.Owner), new("$run", runId)) == 1;

    private static Pr? Find(IWriteScope s, string id) => s.QueryFirst("""
        SELECT version,run_id,state,head,observed_ms,repo,number,reported_by,reported_state,url,owner_session_id FROM engine_pr WHERE id=$id
        """, r => new Pr(r.Long(0), r.IsNull(1) ? null : r.Text(1), r.IsNull(10) ? null : r.Text(10), r.Text(2), r.Text(3), r.IsNull(4) ? null : r.Long(4), r.Text(5), r.Long(6), r.IsNull(7) ? null : r.Text(7), r.IsNull(8) ? null : r.Text(8), r.IsNull(9) ? null : r.Text(9)),
        new SqlArg("$id", id));

    private static EventDraft Changed(CommandContext c, long version, string? runId, string state, long epoch, string? ownerSessionId) =>
        new("pr.changed", "pr", c.EntityId, version, false, new(ownerSessionId, runId, null, null, state, epoch));

    private static CommandResult Refused(CommandContext c, string error, Pr? pr = null, long? epoch = null) =>
        new(false, false, error, pr?.Version ?? 0, 0, epoch ?? c.Epoch);
}
