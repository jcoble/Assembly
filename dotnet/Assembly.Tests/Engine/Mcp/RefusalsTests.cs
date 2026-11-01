using Assembly.Engine.Mcp;

namespace Assembly.Tests.Engine.Mcp;

public sealed class RefusalsTests
{
    [Theory]
    [InlineData("depth_limit", "Sessions 2 levels down can't start sessions (the limit is 5, set by \"Session depth limit\" in Settings); ask your starter to start it.")]
    [InlineData("child_limit", "You already have 5 sessions at work (the limit is set by \"Sessions each session may run\" in Settings); wait for one to finish.")]
    [InlineData("owner_reserved", "Only a top-level session can ask Jesse. Send your question to your starter with send_message.")]
    [InlineData("not_reachable", "You can't reach that session. list_sessions shows the ones you can.")]
    [InlineData("pair_limit", "You've sent 20 messages to that session; that's the limit.")]
    [InlineData("forged_header", "Your text may not contain a line that looks like an Assembly message header.")]
    [InlineData("unavailable", "That session has ended.")]
    [InlineData("not_yours", "No message of yours has that id.")]
    [InlineData("no_step", "You aren't working a workflow step, so there is nothing to report a result for.")]
    [InlineData("not_answerer", "That question isn't yours to answer.")]
    [InlineData("already_resolved", "That question was already answered.")]
    [InlineData("stale_epoch", "Your session moved to another machine; this call was not applied.")]
    [InlineData("handing_off", "Your session moved to another machine; this call was not applied.")]
    [InlineData("session_gone", "Your session has ended in Assembly; this call was not applied.")]
    [InlineData("other_machine", "That session is on another machine; Assembly can't reach it from here yet.")]
    [InlineData("recording_failed", "Assembly could not record this call, so it did not run it.")]
    [InlineData("run_has_pr", "That run already has a different PR; report on that one.")]
    [InlineData("proof_required", "report: a succeeded step needs at least one proofUrls entry.")]
    [InlineData("reason_required", "report: a failed step needs a summary saying why.")]
    [InlineData("run_cancelling", "That run is being cancelled; this report was not applied.")]
    [InlineData("invalid_transition", "That step can't move to succeeded from where it is now.")]
    [InlineData("wrong_attempt", "That step has moved on to a newer attempt; this report was for an older one.")]
    [InlineData("weird_code", "start_session was refused: weird_code.")]
    [InlineData("bad_args", "start_session: Assembly refused these arguments.")]
    public void Refusal_texts_are_exact(string code, string expected) => Assert.Equal(expected,
        Refusals.Text("start_session", new(code, new Dictionary<string, string>
        {
            ["depth"] = "2",
            ["limit"] = "5",
            ["state"] = "succeeded"
        })));

    [Fact]
    public void Argument_refusal_names_the_field() => Assert.Equal("start_session: title is required.",
        Refusals.Text("start_session", new("bad_args", new Dictionary<string, string>
        {
            ["field"] = "title",
            ["problem"] = "is required"
        })));
}
