using System.Text.Json;
using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Mcp;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;
using Assembly.Engine.Sessions;
using Assembly.Engine.Providers;
using Assembly.Engine.Workflows.Execution;

namespace Assembly.Engine.NeedsMe;

public static class NeedTransitions
{
    private enum AnswerOrigin { Raw, Owner, LocalParent, ForwardedParent, ProviderFact }
    private sealed record Need(string Id, long Version, string SessionId, string? RunId, string? StepId,
        string Kind, string Text, string Options, string? RequestId, string? ChatId, string State, long? Deadline, string? AddresseeSessionId, string? Presentation = null);
    private sealed record Session(long Epoch, string State, string? RunId, string? ParentId, string Machine, string Holder, long? ReservedEpoch);

    public static CommandResult Apply(IWriteScope s, CommandContext c, NeedCommand command, MessageExpiryPolicy? expiryPolicy = null,
        Func<string, string, bool>? isConfiguredProvider = null) =>
        ApplyCore(s, c, command, expiryPolicy, null, isConfiguredProvider: isConfiguredProvider);

    internal static CommandResult ApplyOwner(IWriteScope s, CommandContext c, PreparedNeedCommand prepared, MessageExpiryPolicy expiryPolicy,
        Func<string, string, bool>? isConfiguredProvider = null)
    {
        if (c.Actor is not { Kind: ActorKind.Window, Id: "owner" } || prepared.Owner is null ||
            prepared.Command is not ResolveNeed { State: "answered" } command) return Refused(c, "bad_args");
        return ApplyCore(s, c, command, expiryPolicy, prepared.Owner, isConfiguredProvider: isConfiguredProvider);
    }

    internal static CommandResult ApplyLocalParent(IWriteScope s, CommandContext c, McpCaller caller,
        PreparedParentNeed prepared, MessageExpiryPolicy expiryPolicy,
        Func<string, string, bool>? isConfiguredProvider = null)
    {
        if (c.Actor is not { Kind: ActorKind.Session } || c.Actor.Id != caller.SessionId) return Refused(c, "forbidden");
        NeedCommand command = prepared.Request.Action == ParentNeedAction.Escalate ? new EscalateNeed()
            : new ResolveNeed("answered", prepared.Request.Answer);
        return ApplyCore(s, c, command, expiryPolicy, null, prepared, caller, isConfiguredProvider);
    }

    internal static CommandResult ApplyForwardedParent(IWriteScope s, CommandContext c,
        PreparedForwardedParentNeed prepared, MessageExpiryPolicy expiryPolicy)
    {
        if (c.Actor is not { Kind: ActorKind.Session } || c.Actor.Id != prepared.Source.ParentSessionId)
            return Refused(c, "forbidden");
        var caller = new McpCaller(prepared.Source.ParentSessionId, prepared.Source.ParentEpoch);
        NeedCommand command = prepared.Source.Action == ParentNeedAction.Escalate ? new EscalateNeed()
            : new ResolveNeed("answered", prepared.Prepared.Request.Answer);
        return ApplyCore(s, c, command, expiryPolicy, null, prepared.Prepared, caller, forwarded: prepared.Source, parentError: prepared.PreparationError);
    }

    private static CommandResult ApplyCore(IWriteScope s, CommandContext c, NeedCommand command, MessageExpiryPolicy? expiryPolicy,
        PreparedNeedCommand.OwnerPreparation? owner, PreparedParentNeed? parent = null, McpCaller? caller = null,
        Func<string, string, bool>? isConfiguredProvider = null, ParentNeedSource? forwarded = null,
        ParentNeedPreparationError parentError = ParentNeedPreparationError.None)
    {
        if (string.IsNullOrWhiteSpace(c.EntityId)) return Refused(c, "bad_args");
        if (command is OpenNeed open) return Open(s, c, open, expiryPolicy);
        var need = Find(s, c.EntityId);
        if (need is null) return Refused(c, "bad_args");
        var session = FindSession(s, need.SessionId)!;
        if (c.Epoch != session.Epoch) return Refused(c, "stale_epoch", need, session.Epoch);
        if (need.State != "open") return Refused(c, "already_resolved", need);
        if (c.ExpectedVersion != need.Version) return Refused(c, "version_conflict", need);
        var origin = owner is not null ? AnswerOrigin.Owner : forwarded is not null ? AnswerOrigin.ForwardedParent : parent is not null ? AnswerOrigin.LocalParent
            : c.Actor.Kind == ActorKind.System ? AnswerOrigin.ProviderFact : AnswerOrigin.Raw;
        if (s.QueryFirst("SELECT source_kind FROM engine_needs_me WHERE id=$id", r => r.Text(0), new SqlArg("$id", need.Id)) == "workflow_gate")
        {
            if (owner is null || command is not ResolveNeed { State: "answered", Answer: { } answer } ||
                !Workflows.Runs.WorkflowGateRevision.TryReadAnswer(answer, out var decision, out var reply))
                return Refused(c, "forbidden", need);
            var runVersion = s.QueryFirst("SELECT version FROM engine_run WHERE id=$run", r => r.Long(0), new SqlArg("$run", need.RunId));
            var result = Workflows.Runs.WorkflowControlTransitions.Apply(s, c with { EntityId = need.RunId!, ExpectedVersion = runVersion },
                new Workflows.Runs.DecideWorkflowStep(need.StepId!, decision!, reply));
            return result.Applied ? result with { Version = need.Version + 1 } : result;
        }
        if (WorkflowExecutionNeeds.IsWorkflow(s, need.Id))
        {
            if (command is not ResolveNeed workflowAnswer) return Refused(c, "unsupported_command", need);
            if (owner?.Error is { } workflowPreparationError) return Refused(c, workflowPreparationError, need);
            return WorkflowExecutionNeeds.Resolve(s, c, workflowAnswer,
                ownerAuthorized: owner is not null,
                parentAuthorized: parent is not null && caller is not null &&
                    WorkflowParentMatches(s, c, need, session, caller, parent));
        }
        if (parent is not null)
        {
            if (!(forwarded is null ? LocalParentMatches(s, c, need, session, caller!, parent)
                : ForwardedParentMatches(s, c, need, session, forwarded, parent))) return Refused(c, "forbidden", need);
            if (command is EscalateNeed)
            {
                if (parent.Request.Answer is not null || parent.ProviderBinding is not null) return Refused(c, "bad_args", need);
                if (FindSession(s, caller!.SessionId)!.ParentId is not null) return Refused(c, "owner_reserved", need);
                s.Execute("UPDATE engine_needs_me SET addressee_session_id=NULL,version=version+1,updated_ms=$now WHERE id=$id",
                    new("$now", c.NowMs), new("$id", need.Id));
                var updated = need with { Version = need.Version + 1, AddresseeSessionId = null };
                return Commit(s, c, updated, [Changed(c, updated, "needs_me.changed")], []);
            }
        }
        if (command is not ResolveNeed resolve) return Refused(c, "unsupported_command", need);
        if (resolve.State is "answered" or "dismissed" && AuthNeed.Open(s, c.Machine, need.Id) is not null)
        {
            if (c.Actor.Kind != ActorKind.Window && (c.Actor.Kind != ActorKind.Session || origin != AnswerOrigin.LocalParent))
                return Refused(c, "forbidden", need);
            if (owner?.Error is { } preparationError) return Refused(c, preparationError, need);
            var choice = resolve.Answer is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
            var dismissed = resolve.State == "dismissed" && resolve.Answer is null || resolve.State == "answered" && choice == "dismiss";
            if (dismissed)
            {
                s.Execute("""
                    UPDATE engine_needs_me SET state='dismissed',version=version+1,updated_ms=$now,
                        answer_json=NULL,answered_by=$actor,answered_ms=$now,answered_origin_json=$origin WHERE id=$id
                    """, new("$now", c.NowMs), new("$actor", c.Actor.Id), new("$origin", OriginJson(c)), new("$id", need.Id));
                var closed = need with { Version = need.Version + 1, State = "dismissed" };
                List<EventDraft> events = [Changed(c, closed, "needs_me.answered")];
                events.AddRange(AuthNeed.FailDismissedMessages(s, c, need.SessionId));
                return Commit(s, c, closed, events, []);
            }
            if (resolve.State != "answered" || choice != "sign_in") return Refused(c, "bad_args", need);
            var started = SignInTransitions.StartFromNeed(s, c, need.Id, isConfiguredProvider);
            if (!started.Applied) return Refused(c, started.Error ?? "unavailable", need);
            s.Execute("UPDATE engine_needs_me SET version=version+1,updated_ms=$now WHERE id=$id", new("$now", c.NowMs), new("$id", need.Id));
            var waiting = need with { Version = need.Version + 1 };
            return Commit(s, c, waiting, [Changed(c, waiting, "needs_me.changed")], []);
        }
        ProviderAnswerBinding? binding = null;
        if (owner is not null)
        {
            if (session.Machine != c.Machine || session.Holder != "local" || session.ReservedEpoch is not null ||
                session.State is "foreign" or "handing_off" or "ended") return Refused(c, "forbidden", need);
            if (owner.Error is { } error) return Refused(c, error, need);
            if (need.RequestId is not null)
            {
                if (owner.Captured is not { } captured || !MatchesProviderBinding(s, need, session, captured.Binding))
                    return Refused(c, "answer_preparation_required", need);
                binding = captured.Binding;
                resolve = resolve with { Answer = captured.Answer };
            }
        }
        else if (parent is not null && need.RequestId is not null)
        {
            if (parent.ProviderBinding is not { } captured || !MatchesProviderBinding(s, need, session, captured))
                return Refused(c, "answer_preparation_required", need);
            binding = captured;
        }
        else if (need.RequestId is not null && resolve.State == "answered" && c.Actor.Kind == ActorKind.Window)
            return Refused(c, "answer_preparation_required", need);
        if (forwarded is not null && parentError != ParentNeedPreparationError.None)
            return Refused(c, parentError == ParentNeedPreparationError.BadAnswer ? "bad_answer" : "answer_preparation_required", need);
        return Resolve(s, c, need, resolve, expiryPolicy, binding, owner, parent, origin);
    }

    private static bool WorkflowParentMatches(IReadScope s, CommandContext c, Need need, Session owner,
        McpCaller caller, PreparedParentNeed prepared)
    {
        var target = prepared.Target;
        return c.Actor.Kind == ActorKind.Session && c.Actor.Id == caller.SessionId &&
            caller.SessionId == need.SessionId && caller.SessionId == need.AddresseeSessionId &&
            caller.Epoch == owner.Epoch && owner.ParentId is null && owner.Machine == c.Machine &&
            owner.Holder == "local" && owner.ReservedEpoch is null &&
            owner.State is not ("lost" or "ended" or "foreign" or "handing_off") &&
            prepared.Request.Action == ParentNeedAction.Answer && prepared.ProviderBinding is null &&
            prepared.Request.NeedId == target.NeedId && target.NeedId == need.Id && target.NeedVersion == need.Version &&
            target.ChildSessionId == need.SessionId && target.ChildOwnerMachine == owner.Machine &&
            target.ChildEpoch == owner.Epoch && target.ChildHolder == owner.Holder &&
            target.ChildReservedEpoch == owner.ReservedEpoch && target.Kind == need.Kind && !target.ProviderBacked &&
            target.ParentSessionId == caller.SessionId && target.ParentOwnerMachine == owner.Machine &&
            target.AddresseeSessionId == need.AddresseeSessionId && target.ProviderRequestId is null &&
            target.ChatRowId is null && need.RequestId is null && need.ChatId is null &&
            s.QueryFirst("SELECT owner_session_id FROM engine_run WHERE id=$id", r => r.Text(0),
                new SqlArg("$id", need.RunId)) == caller.SessionId;
    }

    private static bool LocalParentMatches(IWriteScope s, CommandContext c, Need need, Session child,
        McpCaller caller, PreparedParentNeed prepared)
    {
        var parent = FindSession(s, caller.SessionId);
        return parent is not null && parent.Epoch == caller.Epoch && parent.Machine == c.Machine &&
            parent.Holder == "local" && parent.ReservedEpoch is null && parent.State is not ("lost" or "ended" or "foreign" or "handing_off") &&
            child.ParentId == caller.SessionId && need.AddresseeSessionId == caller.SessionId &&
            child.Machine == c.Machine && child.Holder == "local" && child.ReservedEpoch is null &&
            child.State is not ("foreign" or "handing_off" or "ended") &&
            ParentTargetMatches(need, child, prepared, caller.SessionId, parent.Machine);
    }

    private static bool ForwardedParentMatches(IWriteScope s, CommandContext c, Need need, Session child,
        ParentNeedSource source, PreparedParentNeed prepared)
    {
        var parent = FindSession(s, source.ParentSessionId);
        return source.SourceMachine != c.Machine && parent is not null && parent.State == "foreign" &&
            parent.Machine == source.SourceMachine && parent.Holder == source.SourceMachine && parent.ReservedEpoch is null &&
            child.ParentId == source.ParentSessionId && need.AddresseeSessionId == source.ParentSessionId &&
            child.Machine == c.Machine && child.Holder == "local" && child.ReservedEpoch is null &&
            child.State is not ("foreign" or "handing_off" or "ended") &&
            ParentTargetMatches(need, child, prepared, source.ParentSessionId, parent.Machine);
    }

    private static bool ParentTargetMatches(Need need, Session child, PreparedParentNeed prepared,
        string parent, string parentMachine)
    {
        var target = prepared.Target;
        return prepared.Request.NeedId == target.NeedId && target.NeedId == need.Id && target.NeedVersion == need.Version &&
            target.ChildSessionId == need.SessionId && target.ChildOwnerMachine == child.Machine && target.ChildEpoch == child.Epoch &&
            target.ChildHolder == child.Holder && target.ChildReservedEpoch == child.ReservedEpoch && target.Kind == need.Kind &&
            target.ProviderBacked == (need.RequestId is not null) && target.ParentSessionId == parent &&
            target.ParentOwnerMachine == parentMachine && target.AddresseeSessionId == need.AddresseeSessionId &&
            target.ProviderRequestId == need.RequestId && target.ChatRowId == need.ChatId;
    }

    // The executor is already gone. Close its questions in the same session transaction,
    // without manufacturing an owner answer or queuing an answer to an ended provider.
    internal static IReadOnlyList<EventDraft> CancelForEndedSession(IWriteScope s, CommandContext c) => s.Query("""
        UPDATE engine_needs_me SET state='cancelled',version=version+1,updated_ms=$now,answer_json=NULL,
          answered_by=$actor,answered_ms=$now,answered_origin_json=$origin
        WHERE session_id=$session AND state='open'
        RETURNING id,version,run_id,step_id,addressee_session_id
        """, r => new EventDraft("needs_me.answered", "needs_me", r.Text(0), r.Long(1), false,
            new(c.EntityId, r.IsNull(2) ? null : r.Text(2), r.IsNull(3) ? null : r.Text(3),
                r.IsNull(4) ? null : r.Text(4), "cancelled", c.Epoch, "session_ended")),
        new SqlArg("$now", c.NowMs), new("$actor", c.Actor.Id), new("$origin", OriginJson(c)), new("$session", c.EntityId));

    public static CommandResult Expire(IWriteScope s, CommandContext c, MessageExpiryPolicy? expiryPolicy = null) => Apply(s, c, new ResolveNeed("expired", null), expiryPolicy);

    public static long? NextDeadline(IAppDatabase db) => db.QueryFirst(
        "SELECT MIN(deadline_ms) FROM engine_needs_me WHERE state='open' AND deadline_ms IS NOT NULL",
        r => r.IsNull(0) ? (long?)null : r.Long(0), default);

    private static CommandResult Open(IWriteScope s, CommandContext c, OpenNeed command, MessageExpiryPolicy? expiryPolicy)
    {
        var providerQuestion = command.ProviderQuestion is not null;
        if (command.ProviderRequestId is not null && !providerQuestion) return Refused(c, "bad_args");
        if (command.Kind is not ("question" or "permission" or "choice" or "confirmation") || command.Options is null ||
            !providerQuestion && (string.IsNullOrWhiteSpace(command.Text) ||
                command.Options.Any(string.IsNullOrWhiteSpace) || command.Options.Distinct().Count() != command.Options.Count ||
                command.Kind == "choice" && command.Options.Count < 2) ||
            command.ProviderRequestId is not null && string.IsNullOrWhiteSpace(command.ProviderRequestId) ||
            command.ChatRowId is not null && string.IsNullOrWhiteSpace(command.ChatRowId))
            return Refused(c, "bad_args");
        var session = FindSession(s, command.SessionId);
        if (session is null) return Refused(c, "bad_args");
        if (c.Epoch != session.Epoch) return Refused(c, "stale_epoch", epoch: session.Epoch);
        if (session.Machine != c.Machine || session.Holder != "local" || session.ReservedEpoch is not null)
            return Refused(c, "forbidden");
        if (c.Actor.Kind != ActorKind.System && (c.Actor.Kind != ActorKind.Session || c.Actor.Id != command.SessionId))
            return Refused(c, "forbidden");
        if (c.Actor.Kind == ActorKind.Session)
        {
            if (session.ParentId is not null) return Refused(c, "owner_reserved");
            if (command.Kind == "permission" || command.StepId is not null ||
                command.Kind == "choice" && command.Options.Count > 8 ||
                command.RunId is not null && s.QueryFirst(
                    "SELECT owner_session_id FROM engine_run WHERE id=$id", r => r.Text(0),
                    new SqlArg("$id", command.RunId)) != command.SessionId)
                return Refused(c, "bad_args");
        }
        NeedPresentation? presentation;
        try { presentation = command.Presentation is not null || providerQuestion ? NeedPresentationInputs.Normalize(command) : null; }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or JsonException)
        { return Refused(c, "bad_args"); }
        if (presentation is not null && !NeedPresentationInputs.Valid(presentation, command)) return Refused(c, "bad_args");
        var presentationJson = presentation is null ? null : JsonSerializer.Serialize(presentation, EngineJson.Default.NeedPresentation);
        var options = JsonSerializer.Serialize(command.Options, EngineJson.Default.IReadOnlyListString);
        var current = command.ProviderRequestId is null ? null : s.QueryFirst(
            "SELECT id FROM engine_needs_me WHERE session_id=$session AND provider_request_id=$request",
            r => r.Text(0), new("$session", command.SessionId), new("$request", command.ProviderRequestId));
        var existing = Find(s, current ?? c.EntityId);
        if (existing is not null)
        {
            var same = existing.SessionId == command.SessionId && existing.RunId == command.RunId &&
                existing.StepId == command.StepId && existing.Kind == command.Kind && existing.Text == command.Text &&
                existing.Options == options && existing.RequestId == command.ProviderRequestId &&
                existing.ChatId == command.ChatRowId && existing.Deadline == command.DeadlineMs &&
                (existing.Presentation is null || SamePresentation(existing.Presentation, presentationJson)) &&
                (!providerQuestion || ProviderQuestionMatches(s, c, command, requireOpen: false));
            return same ? new(true, false, null, existing.Version, 0, session.Epoch)
                : Refused(c, "provider_request_conflict", existing);
        }
        if (providerQuestion && !ProviderQuestionMatches(s, c, command, requireOpen: true)) return Refused(c, "bad_args");
        string? addresseeMachine = null;
        if (session.ParentId is { } parentId)
        {
            var parent = FindSession(s, parentId);
            if (parent is null || (parent.State == "foreign") == (parent.Machine == c.Machine)) return Refused(c, "bad_args");
            addresseeMachine = parent.Machine;
        }
        if (c.ExpectedVersion != 0) return Refused(c, "version_conflict");
        if (session.State is not ("idle" or "running" or "needs_me")) return Refused(c, "invalid_transition");
        if (!ValidLinks(s, command, session)) return Refused(c, "bad_args");
        var number = s.QueryFirst("SELECT next_need_number FROM engine_meta WHERE singleton=1", row => row.Long(0));
        if (number < 1 || number == long.MaxValue || command.Presentation is not null &&
            !NeedPresentationInputs.FitsDisplay(command, c, presentation!, number, session.ParentId, addresseeMachine))
            return Refused(c, "bad_args");
        number = NeedPresentationInputs.AllocateNumber(s);
        s.Execute("""
            INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,run_id,step_id,
                kind,text,options_json,provider_request_id,chat_row_id,state,deadline_ms,addressee_session_id,presentation_json,display_number)
            VALUES($id,1,$machine,$now,$now,$session,$run,$step,$kind,CAST($text AS TEXT),$options,$request,$chat,'open',$deadline,$addressee,$presentation,$number)
            """, new("$id", c.EntityId), new("$machine", c.Machine), new("$now", c.NowMs), new("$session", command.SessionId),
            new("$run", command.RunId), new("$step", command.StepId), new("$kind", command.Kind), new("$text", new UTF8Encoding(false, true).GetBytes(command.Text)),
            new("$options", options), new("$request", command.ProviderRequestId), new("$chat", command.ChatRowId),
            new("$deadline", command.DeadlineMs), new("$addressee", session.ParentId), new("$presentation", presentationJson), new("$number", number));
        var need = new Need(c.EntityId, 1, command.SessionId, command.RunId, command.StepId,
            command.Kind, command.Text, options, command.ProviderRequestId, command.ChatRowId, "open", command.DeadlineMs, session.ParentId, presentationJson);
        List<EventDraft> events = [Changed(c, need, "needs_me.opened")];
        if (command.StepId is not null)
        {
            events.AddRange(s.Query("""
                UPDATE engine_step SET state='waiting',version=version+1,updated_ms=$now WHERE id=$id AND state='running'
                RETURNING version
                """, r => new EventDraft("step.changed", "step", command.StepId, r.Long(0), false,
                    new(command.SessionId, command.RunId, command.StepId, null, "waiting", c.Epoch)),
                new("$now", c.NowMs), new("$id", command.StepId)));
            s.Execute("""
                UPDATE engine_step_attempt SET state='waiting' WHERE step_id=$id AND state='running'
                  AND attempt=(SELECT attempt FROM engine_step WHERE id=$id)
                """, new SqlArg("$id", command.StepId));
        }
        List<EffectDraft> effects = [];
        if (session.ParentId is { } addressee)
            events.AddRange(MessageTransitions.NeedOpened(s, c, need.Id, command, addressee, options, effects, expiryPolicy));
        return Commit(s, c, need, events, effects);
    }

    private static bool ProviderQuestionMatches(IWriteScope s, CommandContext c, OpenNeed command, bool requireOpen)
    {
        if (c.Actor.Kind != ActorKind.System || c.Actor.Id != "adapter" ||
            string.IsNullOrWhiteSpace(command.ProviderRequestId) || string.IsNullOrWhiteSpace(command.ChatRowId) ||
            command.ProviderQuestion is not { ValueKind: JsonValueKind.Object } card ||
            command.Kind is not ("permission" or "question") ||
            !card.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != command.Kind ||
            !card.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String || state.GetString() != "open" ||
            !DisplayText(card, "message", out var message) || !DisplayText(card, "title", out var title) ||
            command.Text != (message ?? title ?? "")) return false;
        if (card.TryGetProperty("options", out var options) && options.ValueKind != JsonValueKind.Null)
        {
            if (options.ValueKind != JsonValueKind.Array || options.GetArrayLength() != command.Options.Count) return false;
            var index = 0;
            foreach (var option in options.EnumerateArray())
                if (option.ValueKind != JsonValueKind.Object || !option.TryGetProperty("optionId", out var id) ||
                    id.ValueKind != JsonValueKind.String || id.GetString() != command.Options[index++]) return false;
        }
        else if (command.Options.Count != 0) return false;
        var stored = s.QueryFirst("SELECT payload FROM rows WHERE session_id=$session AND item_id=$item AND kind='ask' ORDER BY seq DESC LIMIT 1",
            r => JsonDocument.Parse(r.Text(0)).RootElement.Clone(), new SqlArg("$session", command.SessionId), new("$item", "ask:" + command.ChatRowId));
        if (stored.ValueKind != JsonValueKind.Object || requireOpen &&
            (!stored.TryGetProperty("state", out var actualState) || actualState.ValueKind != JsonValueKind.String || actualState.GetString() != "open")) return false;
        return SameQuestion(card, stored) && SameQuestion(stored, card);
    }

    private static bool DisplayText(JsonElement card, string name, out string? value)
    {
        value = null;
        if (!card.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString(); return true;
    }

    private static bool SameQuestion(JsonElement first, JsonElement second)
    {
        foreach (var property in first.EnumerateObject())
            if (property.Name is not ("state" or "answer") && (!second.TryGetProperty(property.Name, out var other) ||
                !JsonElement.DeepEquals(property.Value, other))) return false;
        return true;
    }

    private static bool ValidLinks(IWriteScope s, OpenNeed command, Session session)
    {
        if (command.StepId is not null && command.RunId is null) return false;
        if (command.RunId is not null)
        {
            var run = s.QueryFirst("SELECT owner_session_id,state FROM engine_run WHERE id=$id",
                r => new { Owner = r.Text(0), State = r.Text(1) }, new SqlArg("$id", command.RunId));
            if (run is null || RunTransitions.IsTerminal(run.State) || run.State == "cancelling" ||
                run.Owner != command.SessionId && session.RunId != command.RunId) return false;
        }
        return command.StepId is null || s.QueryFirst("""
            SELECT COUNT(*) FROM engine_step WHERE id=$step AND run_id=$run AND session_id=$session AND state IN ('running','waiting')
            """, r => r.Long(0), new("$step", command.StepId), new("$run", command.RunId), new("$session", command.SessionId)) == 1;
    }

    private static CommandResult Resolve(IWriteScope s, CommandContext c, Need need, ResolveNeed command, MessageExpiryPolicy? expiryPolicy,
        ProviderAnswerBinding? binding, PreparedNeedCommand.OwnerPreparation? owner, PreparedParentNeed? parent, AnswerOrigin origin)
    {
        if (command.State is not ("answered" or "dismissed" or "expired" or "cancelled")) return Refused(c, "bad_args", need);
        var allowed = c.Actor.Kind switch
        {
            ActorKind.Window => command.State is "answered" or "dismissed",
            ActorKind.System => command.State is "answered" or "expired" or "cancelled",
            ActorKind.Session => command.State == "answered" && origin is AnswerOrigin.LocalParent or AnswerOrigin.ForwardedParent,
            _ => false
        };
        if (!allowed) return Refused(c, "forbidden", need);
        if (c.Actor.Kind == ActorKind.System && command.State is "answered" or "cancelled" && string.IsNullOrWhiteSpace(command.Reason))
            return Refused(c, "bad_args", need);
        if (command.State == "expired" && (need.Deadline is null || c.NowMs < need.Deadline))
            return Refused(c, "not_expired", need);
        if (command.State == "answered" && (command.Answer is null || command.Answer.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) ||
            command.State != "answered" && command.Answer is not null) return Refused(c, "bad_args", need);
        var answer = command.State == "answered" ? command.Answer : null;
        if (need.RequestId is null && answer is { } supplied && need.Presentation is { } metadata)
        {
            var presentation = JsonSerializer.Deserialize(metadata, EngineJson.Default.NeedPresentation)!;
            if (presentation.InputMode is not null && !NeedPresentationInputs.AcceptsMcp(presentation, supplied))
                return Refused(c, "bad_args", need);
        }
        var originJson = OriginJson(c);

        s.Execute("""
            UPDATE engine_needs_me SET state=$state,version=version+1,updated_ms=$now,answer_json=$answer,answered_by=$actor,answered_ms=$now,answered_origin_json=$origin
            WHERE id=$id
            """, new("$state", command.State), new("$now", c.NowMs), new("$answer", answer?.GetRawText()),
            new("$actor", c.Actor.Id), new("$origin", originJson), new("$id", need.Id));
        var updated = need with { Version = need.Version + 1, State = command.State };
        List<EffectDraft> effects = [];
        var alreadySettled = c.Actor.Kind == ActorKind.System && (command.State == "answered" ||
            command.State == "cancelled" && command.Reason is "provider_gone" or "turn_cancelled" or "session_ended");
        if (need.RequestId is not null && !alreadySettled)
        {
            ProviderTerminalBinding? cancellation = null;
            if (command.State != "answered" && !string.IsNullOrWhiteSpace(need.ChatId))
            {
                var attempt = need.StepId is null ? null : s.QueryFirst("""
                    SELECT attempt FROM engine_step WHERE id=$step AND session_id=$session AND run_id=$run
                    """, r => (int?)checked((int)r.Long(0)), new SqlArg("$step", need.StepId),
                    new("$session", need.SessionId), new("$run", need.RunId));
                cancellation = new(need.SessionId, need.ChatId, c.Epoch, need.RunId, need.StepId, attempt, command.State);
            }
            var value = answer ?? JsonSerializer.SerializeToElement((string?)null, EngineJson.Default.String);
            var payload = owner?.AnswerPayload ?? parent?.AnswerPayload ?? JsonSerializer.SerializeToElement(
                new NeedAnswer(need.Id, need.RequestId, value, Binding: binding, Cancellation: cancellation), EngineJson.Default.NeedAnswer);
            effects.Add(new($"answer/{need.Id}", "answer", need.SessionId, need.RunId, payload));
        }
        List<EventDraft> events = [Changed(c, updated, "needs_me.answered", command.Reason)];
        if (need.RequestId is null)
            events.AddRange(MessageTransitions.NeedResolved(s, c, need.Id, need.SessionId, command.State, command.Answer, expiryPolicy, owner?.Note ?? parent?.Note));
        return Commit(s, c, updated, events, effects);
    }

    private static bool SamePresentation(string stored, string? incoming)
    {
        if (incoming is null) return false;
        using var first = JsonDocument.Parse(stored);
        using var second = JsonDocument.Parse(incoming);
        return JsonElement.DeepEquals(first.RootElement, second.RootElement);
    }

    internal static string? OriginJson(CommandContext context) => context.Origin is null ? null :
        JsonSerializer.Serialize(context.Origin, EngineJson.Default.CommandOrigin);

    private static bool MatchesProviderBinding(IWriteScope s, Need need, Session session, ProviderAnswerBinding binding)
    {
        if (string.IsNullOrWhiteSpace(need.ChatId) || binding.SessionId != need.SessionId || binding.ChatRowId != need.ChatId ||
            binding.ProviderRequestId != need.RequestId || string.IsNullOrWhiteSpace(binding.AcpSessionId) ||
            binding.Observation is not { } observed || observed.SessionId != need.SessionId || observed.Epoch != session.Epoch ||
            observed.RunId != need.RunId || observed.StepId != need.StepId) return false;
        var current = s.QueryFirst("""
            SELECT id,run_id,attempt FROM engine_step WHERE session_id=$session AND state IN ('running','waiting')
            ORDER BY updated_ms DESC,id LIMIT 1
            """, r => new { Id = r.Text(0), RunId = r.Text(1), Attempt = checked((int)r.Long(2)) }, new SqlArg("$session", need.SessionId));
        return observed == new SessionObservation(need.SessionId, current?.RunId ?? session.RunId, current?.Id, current?.Attempt, session.Epoch);
    }

    private static CommandResult Commit(IWriteScope s, CommandContext c, Need need, List<EventDraft> events, List<EffectDraft> effects)
    {
        events.AddRange(SessionTransitions.RecomputeNeeds(s, c, need.SessionId));
        if (need.State != "open")
            foreach (var released in MessageRelease.Next(s, c, need.SessionId, effects))
            {
                // An inserted answer may be released in this transaction; publish its final revision once.
                var created = events.Any(e => e.EntityKind == "message" && e.EntityId == released.EntityId && e.MessageCreated);
                events.RemoveAll(e => e.EntityKind == "message" && e.EntityId == released.EntityId);
                events.Add(released with { MessageCreated = created });
            }
        if (need.RunId is not null) events.AddRange(RunTransitions.Recompute(s, c, need.RunId));
        return EngineJournal.Commit(s, c, need.Version, c.Epoch, events, effects);
    }

    private static EventDraft Changed(CommandContext c, Need need, string kind, string? reason = null) =>
        new(kind, "needs_me", need.Id, need.Version, false, new(need.SessionId, need.RunId, need.StepId, null, need.State, c.Epoch, reason));
    private static Session? FindSession(IWriteScope s, string id) => s.QueryFirst(
        "SELECT epoch,state,run_id,parent_id,machine,holder,reserved_epoch FROM engine_session WHERE id=$id",
        r => new Session(r.Long(0), r.Text(1), r.IsNull(2) ? null : r.Text(2), r.IsNull(3) ? null : r.Text(3),
            r.Text(4), r.Text(5), r.IsNull(6) ? null : r.Long(6)), new SqlArg("$id", id));
    private static Need? Find(IWriteScope s, string id) => s.QueryFirst("""
        SELECT id,version,session_id,run_id,step_id,kind,CAST(text AS BLOB),options_json,provider_request_id,chat_row_id,state,deadline_ms,addressee_session_id,presentation_json
        FROM engine_needs_me WHERE id=$id
        """, r => new Need(r.Text(0), r.Long(1), r.Text(2), r.IsNull(3) ? null : r.Text(3), r.IsNull(4) ? null : r.Text(4),
            r.Text(5), new UTF8Encoding(false, true).GetString(r.Blob(6)), r.Text(7), r.IsNull(8) ? null : r.Text(8), r.IsNull(9) ? null : r.Text(9),
            r.Text(10), r.IsNull(11) ? null : r.Long(11), r.IsNull(12) ? null : r.Text(12), r.IsNull(13) ? null : r.Text(13)), new SqlArg("$id", id));
    private static CommandResult Refused(CommandContext c, string error, Need? need = null, long? epoch = null) =>
        new(false, false, error, need?.Version ?? 0, 0, epoch ?? c.Epoch);
}
