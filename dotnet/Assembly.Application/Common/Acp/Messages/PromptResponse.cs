namespace Assembly.Application.Common.Acp.Messages;

/// <summary>
/// The turn's end. <c>StopReason</c> is <c>end_turn</c>, <c>max_tokens</c>, <c>max_turn_requests</c>,
/// <c>refusal</c> or <c>cancelled</c>. <c>Usage</c> is the turn's token counts, when the agent reports them.
/// </summary>
public sealed record PromptResponse(string StopReason, PromptUsage? Usage = null);
