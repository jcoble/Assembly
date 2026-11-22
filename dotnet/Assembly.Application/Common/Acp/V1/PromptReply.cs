using Assembly.Application.Common.Acp.Messages;

namespace Assembly.Application.Common.Acp.V1;

/// <summary>A version 1 <c>session/prompt</c> reply: it comes at the end of the turn and carries no message id.</summary>
internal sealed record PromptReply(string? StopReason, UsageReport? Usage);
