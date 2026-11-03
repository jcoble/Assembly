namespace Assembly.Engine.Mcp;

public sealed record McpRefusal(string Code, IReadOnlyDictionary<string, string>? Values = null);

public sealed class McpRefused(McpRefusal refusal) : Exception(refusal.Code)
{
    public McpRefusal Refusal { get; } = refusal;
}

public static class Refusals
{
    public static string Text(string tool, McpRefusal refusal)
    {
        string Value(string key) => refusal.Values?.GetValueOrDefault(key) ?? "{" + key + "}";
        return refusal.Code switch
        {
            "depth_limit" => $"Sessions {Value("depth")} levels down can't start sessions (the limit is {Value("limit")}, set by \"Session depth limit\" in Settings); ask your starter to start it.",
            "child_limit" => $"You already have {Value("limit")} sessions at work (the limit is set by \"Sessions each session may run\" in Settings); wait for one to finish.",
            "owner_reserved" when tool == "start_workflow" => "Only a top-level session can start a workflow; ask your starter to start it.",
            "owner_reserved" => "Only a top-level session can ask Jesse. Send your question to your starter with send_message.",
            "not_reachable" => "You can't reach that session. list_sessions shows the ones you can.",
            "pair_limit" => "You've reached the message limit for that session.",
            "forged_header" => "Your text may not contain a line that looks like an Assembly message header.",
            "unavailable" => "That session has ended.",
            "not_yours" => "No message of yours has that id.",
            "no_step" => "You aren't working a workflow step, so there is nothing to report a result for.",
            "bad_answer" => "Use an exact optionId supplied by the question, or its original form action and content object.",
            "not_answerer" => "That question isn't yours to answer.",
            "already_resolved" => "That question was already answered.",
            "stale_epoch" => "Your session moved to another machine; this call was not applied.",
            "handing_off" => "Your session moved to another machine; this call was not applied.",
            "session_gone" => "Your session has ended in Assembly; this call was not applied.",
            "other_machine" => "That session is on another machine; Assembly can't reach it from here yet.",
            "recording_failed" => "Assembly could not record this call, so it did not run it.",
            "run_has_pr" => "That run already has a different PR; report on that one.",
            "proof_required" => "report: a succeeded step needs at least one proofUrls entry.",
            "reason_required" => "report: a failed step needs a summary saying why.",
            "run_cancelling" => "That run is being cancelled; this report was not applied.",
            "invalid_transition" => $"That step can't move to {Value("state")} from where it is now.",
            "wrong_attempt" => "That step has moved on to a newer attempt; this report was for an older one.",
            "bad_args" when refusal.Values?.ContainsKey("field") == true
                && refusal.Values.ContainsKey("problem") => $"{tool}: {Value("field")} {Value("problem")}.",
            "bad_args" => $"{tool}: Assembly refused these arguments.",
            _ => $"{tool} was refused: {refusal.Code}."
        };
    }
}
