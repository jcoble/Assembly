using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Handoffs;
using Assembly.Application.Features.Engine.Messages;
using Assembly.Application.Features.Engine.NeedsMe;
using Assembly.Application.Features.Engine.Persistence;
using Assembly.Application.Features.Engine.Runs;
using Assembly.Application.Features.Engine.Sessions;

namespace Assembly.Application.Features.Engine.Effects;

public static class EffectTransitions
{
    // Shared by selection and the SQL claim guard; ids are deliberately not an ordering key.
    public const string NoEarlierPending = """
        NOT EXISTS (SELECT 1 FROM engine_outbox earlier
          WHERE earlier.session_id=engine_outbox.session_id AND earlier.state IN ('pending','inflight')
          AND (earlier.event_seq<engine_outbox.event_seq OR
            (earlier.event_seq=engine_outbox.event_seq AND earlier.rowid<engine_outbox.rowid)))
        """;

    public static Effect? Read(IAppDatabase db, string effectId, CancellationToken cancellationToken) => db.QueryFirst(
        "SELECT id,kind,session_id,run_id,payload_json,attempt FROM engine_outbox WHERE id=$id", Map,
        cancellationToken, new SqlArg("$id", effectId));

    private static Effect Map(IRow r) => new(r.Text(0), r.Text(1), r.IsNull(2) ? null : r.Text(2),
        r.IsNull(3) ? null : r.Text(3), ParsePayload(r.Text(4)), checked((int)r.Long(5)));

    private static JsonElement ParsePayload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static CommandResult Apply(IWriteScope s, CommandContext c, EffectCommand command)
    {
        var row = s.QueryFirst("SELECT version,state,attempt,next_ms FROM engine_outbox WHERE id=$id",
            r => new { Version = r.Long(0), State = r.Text(1), Attempt = r.Long(2), Next = r.IsNull(3) ? (long?)null : r.Long(3) }, new SqlArg("$id", c.EntityId));
        CommandResult Reject(string reason) => new(false, false, reason, row?.Version ?? 0, 0, c.Epoch);
        if (c.Actor.Kind != ActorKind.System) return Reject("forbidden");
        if (row is null) return Reject("bad_args");
        if (row.Version != c.ExpectedVersion) return Reject("version_conflict");
        string state;
        long? next = null;
        EffectReceipt? receipt = null;
        switch (command)
        {
            case ClaimEffect:
                if (row.State is not ("pending" or "uncertain") || row.Next > c.NowMs) return Reject("not_due");
                if (s.Execute($"""
                    UPDATE engine_outbox SET state='inflight',attempt=attempt+1,version=version+1,updated_ms=$now,next_ms=NULL
                    WHERE id=$id AND version=$version AND state IN ('pending','uncertain')
                      AND (next_ms IS NULL OR next_ms<=$now) AND {NoEarlierPending}
                    """, new("$id", c.EntityId), new("$version", c.ExpectedVersion), new("$now", c.NowMs)) != 1)
                    return Reject("unavailable");
                return Commit(s, c, row.Version + 1, "inflight");
            case RecoverEffect when row.State == "inflight":
                state = "uncertain";
                receipt = new(EffectDisposition.Unknown, JsonSerializer.SerializeToElement((string?)null, EngineJson.Default.String), "interrupted");
                break;
            case EnableEffect when row.State == "unavailable":
                state = s.QueryFirst("SELECT json_extract(receipt_json,'$.disposition') FROM engine_outbox WHERE id=$id",
                    r => !r.IsNull(0) && r.Long(0) == (long)EffectDisposition.Unknown, new SqlArg("$id", c.EntityId)) ? "uncertain" : "pending";
                break;
            case CompleteEffect complete when row.State == "inflight":
                receipt = complete.Receipt;
                if (!Enum.IsDefined(receipt.Disposition)) return Reject("bad_args");
                state = receipt.Disposition switch
                {
                    EffectDisposition.Confirmed => "done",
                    EffectDisposition.TransientFailure => "pending",
                    EffectDisposition.Unavailable => "unavailable",
                    EffectDisposition.Failed => "failed",
                    _ => "uncertain"
                };
                if (state is "pending" or "uncertain") next = checked(c.NowMs + RetryDelayMs(row.Attempt));
                break;
            default: return Reject("invalid_transition");
        }
        var effect = s.QueryFirst("SELECT id,kind,session_id,run_id,payload_json,attempt FROM engine_outbox WHERE id=$id", Map, new SqlArg("$id", c.EntityId))!;
        // Validate receipts before touching the row; the provider can report data, never an injected command.
        if (state == "done" && !ValidConfirmation(effect, receipt!)) return Reject("bad_receipt");
        // Preserve uncertainty while the reconciliation service is unavailable.
        if (state == "unavailable" && s.QueryFirst("SELECT json_extract(receipt_json,'$.disposition') FROM engine_outbox WHERE id=$id",
                r => !r.IsNull(0) && r.Long(0) == (long)EffectDisposition.Unknown, new SqlArg("$id", c.EntityId)))
            receipt = receipt! with { Disposition = EffectDisposition.Unknown };
        s.Execute("""
            UPDATE engine_outbox SET state=$state,version=version+1,updated_ms=$now,next_ms=$next,
                receipt_json=COALESCE($receipt,receipt_json),last_error=$error WHERE id=$id
            """, new("$state", state), new("$now", c.NowMs), new("$next", next), new("$id", c.EntityId),
            new("$receipt", receipt is null ? null : JsonSerializer.Serialize(receipt, EngineJson.Default.EffectReceipt)), new("$error", receipt?.Error));
        if (state == "done") Confirm(s, c, effect, receipt!);
        return Commit(s, c, row.Version + 1, state);
    }

    private static long RetryDelayMs(long attempt) => Math.Min(1000L << (int)Math.Clamp(attempt - 1, 0, 6), 60000);

    private static bool ValidConfirmation(Effect effect, EffectReceipt receipt)
    {
        try
        {
            return effect.Kind switch
            {
                "answer" => receipt.Data.Deserialize(EngineJson.Default.AnswerEffectData)?.ProviderRequestId ==
                    effect.Payload.Deserialize(EngineJson.Default.NeedAnswer)?.ProviderRequestId,
                "start" => receipt.Data.Deserialize(EngineJson.Default.SessionEffectData)?.State is "idle" or "lost" or "ended",
                "stop" => receipt.Data.Deserialize(EngineJson.Default.SessionEffectData)?.State is "idle" or "ended",
                "send" => receipt.Data.Deserialize(EngineJson.Default.MessageEffectData)?.State is "delivered_to_turn" or "next_turn" or "unsupported" or "failed",
                "handoff" => receipt.Data.Deserialize(EngineJson.Default.HandoffEffectData) is { State: "accepted" or "resumed" or "failed" } handoff &&
                    handoff.NextEpoch == effect.Payload.Deserialize(EngineJson.Default.HandoffTransfer)!.NextEpoch,
                _ => false
            };
        }
        catch (JsonException) { return false; }
    }

    private static void Confirm(IWriteScope s, CommandContext c, Effect effect, EffectReceipt receipt)
    {
        var session = s.QueryFirst("SELECT version,epoch,state,cancel_requested FROM engine_session WHERE id=$id",
            r => (Version: r.Long(0), Epoch: r.Long(1), State: r.Text(2), CancelRequested: r.Long(3) != 0), new SqlArg("$id", effect.SessionId));
        var target = c with { EntityId = effect.SessionId!, ExpectedVersion = session.Version, Epoch = session.Epoch };
        CommandResult? result = null;
        if (effect.Kind is "start" or "stop")
        {
            var data = receipt.Data.Deserialize(EngineJson.Default.SessionEffectData)!;
            // Two independently queued stop intents can observe the same already-acknowledged stop.
            if (effect.Kind != "stop" || session.State != data.State || session.CancelRequested)
                result = SessionTransitions.Apply(s, target, new SetSessionState(data.State, data.ProviderSessionId, data.Reason), 0);
            if (effect.Kind == "stop" && result is not { Applied: false }) CancelStoppedSteps(s, target);
        }
        if (effect.Kind == "send")
        {
            var data = receipt.Data.Deserialize(EngineJson.Default.MessageEffectData)!;
            var id = effect.Id["send/".Length..];
            var version = s.QueryFirst("SELECT version FROM engine_message WHERE id=$id", r => r.Long(0), new SqlArg("$id", id));
            result = MessageTransitions.Apply(s, target with { EntityId = id, ExpectedVersion = version }, new MessageOutcome(data.State, data.ProviderTurnId, data.Reason));
        }
        if (effect.Kind == "handoff")
        {
            var intent = effect.Payload.Deserialize(EngineJson.Default.HandoffTransfer)!;
            var data = receipt.Data.Deserialize(EngineJson.Default.HandoffEffectData)!;
            var version = s.QueryFirst("SELECT version FROM engine_handoff WHERE id=$id", r => r.Long(0), new SqlArg("$id", intent.HandoffId));
            result = HandoffTransitions.Apply(s, target with
            {
                EntityId = intent.HandoffId,
                ExpectedVersion = version,
                Epoch = intent.NextEpoch,
                Actor = new(ActorKind.System, intent.NewHolder)
            }, new HandoffOutcome(data.State, data.NextEpoch, data.Reason));
        }
        if (result is { Applied: false }) throw new InvalidOperationException($"Effect {effect.Id} confirmation rejected: {result.Error}");
        if (effect.RunId is not null)
            EngineJournal.Commit(s, c, c.ExpectedVersion + 1, c.Epoch, RunTransitions.Recompute(s, c, effect.RunId), []);
    }

    private static void CancelStoppedSteps(IWriteScope s, CommandContext c)
    {
        var events = s.Query("""
            UPDATE engine_step SET state='cancelled',version=version+1,updated_ms=$now,ended_ms=$now
            WHERE session_id=$id AND state IN ('running','waiting')
            RETURNING id,version,run_id
            """, r => new EventDraft("step.changed", "step", r.Text(0), r.Long(1), false,
                new(c.EntityId, r.Text(2), r.Text(0), null, "cancelled", c.Epoch)), new("$now", c.NowMs), new("$id", c.EntityId));
        s.Execute("""
            UPDATE engine_step_attempt SET state='cancelled',ended_ms=$now
            WHERE state IN ('running','waiting') AND EXISTS(SELECT 1 FROM engine_step st
                WHERE st.id=step_id AND st.attempt=engine_step_attempt.attempt AND st.session_id=$id AND st.state='cancelled')
            """, new("$now", c.NowMs), new("$id", c.EntityId));
        foreach (var run in events.Select(e => e.Data.RunId!).Distinct().ToArray())
            events.AddRange(RunTransitions.Recompute(s, c, run));
        EngineJournal.Commit(s, c, c.ExpectedVersion, c.Epoch, events, []);
    }

    private static CommandResult Commit(IWriteScope s, CommandContext c, long version, string state)
    {
        var effect = s.QueryFirst("SELECT session_id,run_id FROM engine_outbox WHERE id=$id",
            r => (Session: r.IsNull(0) ? null : r.Text(0), Run: r.IsNull(1) ? null : r.Text(1)), new SqlArg("$id", c.EntityId));
        return EngineJournal.Commit(s, c, version, c.Epoch,
            [new("effect.changed", "effect", c.EntityId, version, false, new(effect.Session, effect.Run, null, null, state, c.Epoch))], []);
    }
}
