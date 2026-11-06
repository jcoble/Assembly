using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Prs;

internal static class PrObservationTransitions
{
    internal static CommandResult Apply(IWriteScope s, CommandContext c, PrCommand command)
    {
        if (c.Actor.Kind != ActorKind.System) return Refused(c, "forbidden");
        return command switch
        {
            RecordPrObservation record => Record(s, c, record),
            ApplyPrObservation apply => c.Actor.Id != "machine:" + apply.OriginMachine
                ? Refused(c, "forbidden") : Refused(c, "workflow_ref_unavailable"),
            PrObservationRecorded ack => Ack(s, c, ack),
            _ => Refused(c, "bad_args")
        };
    }

    private static CommandResult Record(IWriteScope s, CommandContext c, RecordPrObservation command)
    {
        var f = command.Facts;
        if (f is null || !Guid.TryParse(c.EntityId, out var observationId) || observationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.CoordinatorMachine) || string.IsNullOrWhiteSpace(f.SessionId) ||
            string.IsNullOrWhiteSpace(f.Repo) || f.Number <= 0 || string.IsNullOrWhiteSpace(f.Head) ||
            f.State is not ("open" or "closed" or "merged" or "unknown") || f.ObservedMs < 0 ||
            !Uri.TryCreate(f.Url, UriKind.Absolute, out var url) || url.Scheme != "https")
            return Refused(c, "bad_args");
        var json = JsonSerializer.Serialize(f, EngineJson.Default.PrObservationFacts);
        if (Encoding.UTF8.GetByteCount(json) > 24 * 1024) return Refused(c, "command_too_large");
        var prior = s.QueryFirst("""
            SELECT version,origin_machine,coordinator_machine,facts_json,state,coordinator_seq
            FROM engine_pr_observation WHERE id=$id
            """, r => new
        {
            Version = r.Long(0),
            Origin = r.Text(1),
            Coordinator = r.Text(2),
            Facts = r.Text(3),
            State = r.Text(4),
            Seq = r.IsNull(5) ? 0 : r.Long(5)
        }, new SqlArg("$id", c.EntityId));
        if (prior is not null)
            return prior.Origin == c.Machine && prior.Coordinator == command.CoordinatorMachine && prior.Facts == json
                ? new(true, false, null, prior.Version, prior.Seq, f.SessionEpoch) : Refused(c, "observation_conflict");
        if (c.ExpectedVersion != 0) return Refused(c, "version_conflict");
        var source = s.QueryFirst("""
            SELECT epoch,run_id,task_url FROM engine_session WHERE id=$id AND machine=$machine AND holder='local'
                AND reserved_epoch IS NULL AND state NOT IN ('lost','ended','foreign','handing_off')
            """, r => new { Epoch = r.Long(0), Run = r.IsNull(1) ? null : r.Text(1), Task = r.IsNull(2) ? null : r.Text(2) },
            new("$id", f.SessionId), new("$machine", c.Machine));
        if (source is null) return Refused(c, "forbidden");
        if (source.Epoch != f.SessionEpoch || c.Epoch != source.Epoch) return Refused(c, "stale_epoch");
        // Adopted workflow facts require the canonical workflow run reference, which is not supplied here.
        if (command.CoordinatorMachine != c.Machine) return Refused(c, "workflow_ref_unavailable");
        if (source.Run != f.RunId) return Refused(c, "forbidden");
        var run = f.RunId is null ? null : s.QueryFirst("""
            SELECT version,owner_session_id,pr_id,state,task_url FROM engine_run WHERE id=$run AND machine=$machine
                AND (owner_session_id=$session OR EXISTS(SELECT 1 FROM engine_step st
                    WHERE st.run_id=engine_run.id AND st.session_id=$session AND st.state IN ('running','waiting')))
            """, r => new { Version = r.Long(0), Owner = r.Text(1), Pr = r.IsNull(2) ? null : r.Text(2), State = r.Text(3), Task = r.IsNull(4) ? null : r.Text(4) },
            new("$run", f.RunId), new("$machine", c.Machine), new("$session", f.SessionId));
        if (f.RunId is not null && run is null || f.StepId is not null && (f.RunId is null ||
            s.QueryFirst("SELECT COUNT(*) FROM engine_step WHERE id=$step AND run_id=$run AND session_id=$session AND state IN ('running','waiting')",
                r => r.Long(0), new("$step", f.StepId), new("$run", f.RunId), new("$session", f.SessionId)) != 1))
            return Refused(c, "forbidden");
        var pr = s.QueryFirst("""
            SELECT id,version,run_id,owner_session_id,state,observed_ms FROM engine_pr WHERE repo=$repo AND number=$number
            """, r => new
        {
            Id = r.Text(0),
            Version = r.Long(1),
            Run = r.IsNull(2) ? null : r.Text(2),
            Owner = r.IsNull(3) ? null : r.Text(3),
            State = r.Text(4),
            Observed = r.IsNull(5) ? (long?)null : r.Long(5)
        },
            new("$repo", f.Repo), new("$number", f.Number));
        if (pr is not null && (pr.Run != f.RunId || f.RunId is null && pr.Owner != f.SessionId))
            return Refused(c, "forbidden");
        var prId = pr?.Id ?? c.EntityId;
        if (pr is null && s.QueryFirst("SELECT COUNT(*) FROM engine_pr WHERE id=$id", r => r.Long(0), new SqlArg("$id", prId)) != 0)
            return Refused(c, "observation_conflict");
        s.Execute("""
            INSERT INTO engine_pr_observation(id,version,machine,created_ms,updated_ms,origin_machine,coordinator_machine,
                session_id,run_id,step_id,epoch,facts_json,state)
            VALUES($id,1,$machine,$now,$now,$machine,$machine,$session,$run,$step,$epoch,$facts,'pending')
            """, new("$id", c.EntityId), new("$machine", c.Machine), new("$now", c.NowMs),
            new("$session", f.SessionId), new("$run", f.RunId), new("$step", f.StepId), new("$epoch", f.SessionEpoch), new("$facts", json));
        List<EventDraft> events = [new("pr.observation_pending", "pr_observation", c.EntityId, 1, false,
            new(f.SessionId,f.RunId,f.StepId,null,"pending",f.SessionEpoch))];
        if (pr is null)
        {
            s.Execute("""
                INSERT INTO engine_pr(id,version,machine,created_ms,updated_ms,task_url,run_id,owner_session_id,repo,number,head,
                    state,observed_head,observed_ms,url)
                VALUES($id,1,$machine,$now,$now,$task,$run,$owner,$repo,$number,$head,$state,$head,$observed,$url)
                """, new("$id", prId), new("$machine", c.Machine), new("$now", c.NowMs), new("$task", f.RunId is null ? source.Task : run?.Task), new("$run", f.RunId),
                new("$owner", f.RunId is null ? f.SessionId : null), new("$repo", f.Repo), new("$number", f.Number),
                new("$head", f.Head), new("$state", f.State), new("$observed", f.ObservedMs), new("$url", f.Url));
            events.Add(new("pr.changed", "pr", prId, 1, false, new(f.SessionId, f.RunId, f.StepId, null, f.State, f.SessionEpoch)));
        }
        else if ((pr.Observed is null || f.ObservedMs > pr.Observed) && pr.State is not ("merged" or "closed"))
        {
            s.Execute("""
                UPDATE engine_pr SET version=version+1,updated_ms=$now,state=$state,observed_head=$head,observed_ms=$observed,url=$url WHERE id=$id
                """, new("$now", c.NowMs), new("$state", f.State), new("$head", f.Head), new("$observed", f.ObservedMs), new("$url", f.Url), new("$id", prId));
            events.Add(new("pr.changed", "pr", prId, pr.Version + 1, false, new(f.SessionId, f.RunId, f.StepId, null, f.State, f.SessionEpoch)));
        }
        if (run is not null && run.Pr is null)
        {
            s.Execute("UPDATE engine_run SET pr_id=$pr,version=version+1,updated_ms=$now WHERE id=$run",
                new("$pr", prId), new("$now", c.NowMs), new("$run", f.RunId));
            events.Add(new("run.changed", "run", f.RunId!, run.Version + 1, false, new(run.Owner, f.RunId, null, null, run.State, f.SessionEpoch)));
        }
        events.Add(new("pr.observation_recorded", "pr_observation", c.EntityId, 2, false, new(f.SessionId, f.RunId, f.StepId, null, "recorded", f.SessionEpoch)));
        var result = EngineJournal.Commit(s, c, 2, f.SessionEpoch, events, []);
        s.Execute("UPDATE engine_pr_observation SET version=2,state='recorded',coordinator_seq=$seq WHERE id=$id", new("$seq", result.Seq), new("$id", c.EntityId));
        return result;
    }

    private static CommandResult Ack(IWriteScope s, CommandContext c, PrObservationRecorded ack)
    {
        if (ack.ObservationId != c.EntityId || ack.CoordinatorSeq <= 0 || c.Actor.Id != "machine:" + ack.CoordinatorMachine)
            return Refused(c, "forbidden");
        var intent = s.QueryFirst("""
            SELECT version,state,coordinator_seq,epoch FROM engine_pr_observation
            WHERE id=$id AND origin_machine=$machine AND coordinator_machine=$coordinator
            """, r => new { Version = r.Long(0), State = r.Text(1), Seq = r.IsNull(2) ? 0 : r.Long(2), Epoch = r.Long(3) },
            new("$id", c.EntityId), new("$machine", c.Machine), new("$coordinator", ack.CoordinatorMachine));
        if (intent is null) return Refused(c, "forbidden");
        if (intent.Epoch != c.Epoch) return Refused(c, "stale_epoch");
        if (intent.State == "recorded") return intent.Seq == ack.CoordinatorSeq
            ? new(true, false, null, intent.Version, 0, intent.Epoch) : Refused(c, "observation_conflict");
        if (intent.Version != c.ExpectedVersion) return Refused(c, "version_conflict");
        s.Execute("UPDATE engine_pr_observation SET version=version+1,state='recorded',coordinator_seq=$seq,updated_ms=$now WHERE id=$id",
            new("$seq", ack.CoordinatorSeq), new("$now", c.NowMs), new("$id", c.EntityId));
        return EngineJournal.Commit(s, c, intent.Version + 1, intent.Epoch,
            [new("pr.observation_recorded", "pr_observation", c.EntityId, intent.Version + 1, false, new(null, null, null, null, "recorded", intent.Epoch))], []);
    }

    private static CommandResult Refused(CommandContext c, string error) => new(false, false, error, c.ExpectedVersion, 0, c.Epoch);
}
