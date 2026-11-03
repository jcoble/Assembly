using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;

namespace Assembly.Engine.Handoffs;

public static class HandoffTransitions
{
    private sealed record Session(long Version, long Epoch, long? ReservedEpoch, string State,
        string Holder, string? RunId, string? ParentId, string Machine, string Provider, string AccountContext, string? ProviderSessionId);
    private sealed record Handoff(long Version, string SessionId, string OldHolder, string NewHolder,
        long OldEpoch, long NextEpoch, string State);

    public static CommandResult Apply(IWriteScope s, CommandContext c, HandoffCommand command)
    {
        if (string.IsNullOrWhiteSpace(c.EntityId)) return Refused(c, "bad_args");
        if (command is RequestHandoff request) return Request(s, c, request);
        var handoff = Find(s, c.EntityId);
        if (handoff is null) return Refused(c, "bad_args");
        if (command is not HandoffOutcome outcome) return Refused(c, "unsupported_command", handoff.Version, handoff.NextEpoch);
        return Refused(c, "forbidden", handoff.Version, handoff.NextEpoch);
    }

    internal static CommandResult ApplyAdmitted(IWriteScope scope, HandoffAdmission admission)
    {
        admission.ValidateBindings(scope, admission.Context, admission.Key, admission.Canonical);
        var handoff = Find(scope, admission.Identity.HandoffId);
        if (handoff is null || admission.Outcome is null) return Refused(admission.Context, "bad_args");
        return Outcome(scope, admission.Context, handoff, admission.Outcome);
    }

    private static CommandResult Request(IWriteScope s, CommandContext c, RequestHandoff command)
    {
        var session = FindSession(s, command.SessionId);
        if (session is null) return Refused(c, "bad_args");
        var epoch = session.ReservedEpoch ?? session.Epoch;
        if (session.State == "handing_off") return Refused(c, "handing_off", session.Version, epoch);
        if (c.Epoch != session.Epoch) return Refused(c, "stale_epoch", session.Version, epoch);
        if (c.ExpectedVersion != session.Version) return Refused(c, "version_conflict", session.Version, epoch);
        if (c.Actor.Kind != ActorKind.Window) return Refused(c, "forbidden", session.Version, epoch);
        if (session.Machine != c.Machine || session.Holder != "local" && session.Holder != c.Machine)
            return Refused(c, "writer_elsewhere", session.Version, epoch);
        if (string.IsNullOrWhiteSpace(command.NewHolder) || command.NewHolder == session.Holder || command.NewHolder == c.Machine)
            return Refused(c, "bad_args", session.Version, epoch);
        if (session.State is "ended" or "moved" || Find(s, c.EntityId) is not null)
            return Refused(c, "invalid_transition", session.Version, epoch);
        if (session.State != "idle") return Refused(c, "busy", session.Version, epoch);
        if (s.QueryFirst("SELECT checkout_state FROM engine_session WHERE id=$id", r => r.Text(0),
                new SqlArg("$id", command.SessionId)) == "pending") return Refused(c, "checkout_pending", session.Version, epoch);
        if (session.ParentId is not null) return Refused(c, "has_parent", session.Version, epoch);
        if (s.QueryFirst("SELECT EXISTS(SELECT 1 FROM engine_needs_me WHERE session_id=$id AND state='open')",
            row => row.Long(0), new SqlArg("$id", command.SessionId)) != 0)
            return Refused(c, "needs_attention", session.Version, epoch);
        if (s.QueryFirst("SELECT EXISTS(SELECT 1 FROM engine_session WHERE parent_id=$id AND state NOT IN ('ended','moved'))",
            row => row.Long(0), new SqlArg("$id", command.SessionId)) != 0)
            return Refused(c, "children_running", session.Version, epoch);
        var nextEpoch = checked(session.Epoch + 1);
        s.Execute("""
            INSERT INTO engine_handoff(id,version,machine,created_ms,updated_ms,session_id,old_holder,new_holder,old_epoch,next_epoch,state)
            VALUES($id,1,$machine,$now,$now,$session,$old,$new,$epoch,$next,'requested')
            """, new("$id", c.EntityId), new("$machine", c.Machine), new("$now", c.NowMs),
            new("$session", command.SessionId), new("$old", session.Holder), new("$new", command.NewHolder),
            new("$epoch", session.Epoch), new("$next", nextEpoch));
        s.Execute("""
            UPDATE engine_session SET version=version+1,updated_ms=$now,state='handing_off',reserved_epoch=$next WHERE id=$id
            """, new("$now", c.NowMs), new("$next", nextEpoch), new("$id", command.SessionId));
        var handoff = new Handoff(1, command.SessionId, session.Holder, command.NewHolder, session.Epoch, nextEpoch, "requested");
        return Commit(s, c, handoff, session with { Version = session.Version + 1, State = "handing_off", ReservedEpoch = nextEpoch },
            session.RunId,
            [new("", "release", command.SessionId, session.RunId,
                JsonSerializer.SerializeToElement(new HandoffReleaseEffect(
                    new(c.EntityId, c.Machine, command.SessionId, command.NewHolder, session.Epoch, nextEpoch),
                    session.Provider, session.AccountContext, session.ProviderSessionId), EngineJson.Default.HandoffReleaseEffect))]);
    }

    private static CommandResult Outcome(IWriteScope s, CommandContext c, Handoff handoff, HandoffOutcome command)
    {
        CommandResult Reject(string error) => Refused(c, error, handoff.Version, handoff.NextEpoch);
        if (c.Actor != new EngineActor(ActorKind.System, "machine:" + handoff.NewHolder))
            return Reject("forbidden");
        if (c.Epoch != handoff.NextEpoch || command.NextEpoch != handoff.NextEpoch) return Reject("stale_epoch");
        if (c.ExpectedVersion != handoff.Version) return Reject("version_conflict");
        if ((handoff.State, command.State) is not (("requested", "accepted" or "failed") or ("accepted", "resumed" or "failed")))
            return Reject("invalid_transition");
        var readiness = EngineJournal.CheckHandoffSourceReady(s, c.Machine,
            new(c.EntityId, c.Machine, handoff.SessionId, handoff.NewHolder, handoff.OldEpoch, handoff.NextEpoch),
            command.State == "accepted");
        if (readiness is not null) return Reject(readiness.Reason ?? "unavailable");
        s.Execute("UPDATE engine_handoff SET version=version+1,updated_ms=$now,state=$state,reason=$reason WHERE id=$id",
            new("$now", c.NowMs), new("$state", command.State), new("$reason", command.Reason), new("$id", c.EntityId));
        var session = FindSession(s, handoff.SessionId)!;
        Session? changedSession = null;
        if (command.State is "resumed" or "failed")
        {
            changedSession = session with
            {
                Version = session.Version + 1,
                ReservedEpoch = null,
                Epoch = command.State == "resumed" ? handoff.NextEpoch : handoff.OldEpoch,
                Holder = command.State == "resumed" ? handoff.NewHolder : handoff.OldHolder,
                State = command.State == "resumed" ? "moved" : "idle"
            };
            s.Execute("""
                UPDATE engine_session SET version=version+1,updated_ms=$now,state=$state,holder=$holder,epoch=$epoch,
                    reserved_epoch=NULL,activity_state='idle' WHERE id=$id
                """, new("$now", c.NowMs), new("$state", changedSession.State), new("$holder", changedSession.Holder),
                new("$epoch", changedSession.Epoch), new("$id", handoff.SessionId));
        }
        return Commit(s, c, handoff with { Version = handoff.Version + 1, State = command.State }, changedSession, session.RunId, []);
    }

    private static CommandResult Commit(IWriteScope s, CommandContext c, Handoff handoff, Session? session, string? runId,
        IReadOnlyList<EffectDraft> effects)
    {
        List<EventDraft> events = [new($"handoff.{handoff.State}", "handoff", c.EntityId, handoff.Version, false,
            new(handoff.SessionId, runId, null, null, handoff.State, handoff.NextEpoch))];
        if (session is not null)
        {
            events.Add(new("session.changed", "session", handoff.SessionId, session.Version, false,
                new(handoff.SessionId, session.RunId, null, session.ParentId, session.State, session.ReservedEpoch ?? session.Epoch)));
            var runs = s.Query("""
                SELECT id FROM engine_run WHERE owner_session_id=$session
                UNION SELECT run_id FROM engine_step WHERE session_id=$session
                """, r => r.Text(0), new SqlArg("$session", handoff.SessionId));
            foreach (var run in runs) events.AddRange(RunTransitions.Recompute(s, c, run));
        }
        return EngineJournal.Commit(s, c, handoff.Version, handoff.NextEpoch, events, effects);
    }

    private static Session? FindSession(IWriteScope s, string id) => s.QueryFirst("""
        SELECT version,epoch,reserved_epoch,state,holder,run_id,parent_id,machine,provider,account_context,provider_session_id FROM engine_session WHERE id=$id
        """, r => new Session(r.Long(0), r.Long(1), r.IsNull(2) ? null : r.Long(2), r.Text(3), r.Text(4),
            r.IsNull(5) ? null : r.Text(5), r.IsNull(6) ? null : r.Text(6), r.Text(7), r.Text(8), r.Text(9), r.IsNull(10) ? null : r.Text(10)), new SqlArg("$id", id));

    private static Handoff? Find(IWriteScope s, string id) => s.QueryFirst("""
        SELECT version,session_id,old_holder,new_holder,old_epoch,next_epoch,state FROM engine_handoff WHERE id=$id
        """, r => new Handoff(r.Long(0), r.Text(1), r.Text(2), r.Text(3), r.Long(4), r.Long(5), r.Text(6)), new SqlArg("$id", id));

    private static CommandResult Refused(CommandContext c, string error, long version = 0, long? epoch = null) =>
        new(false, false, error, version, 0, epoch ?? c.Epoch);
}
