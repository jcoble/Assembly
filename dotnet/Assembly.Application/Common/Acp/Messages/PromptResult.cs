namespace Assembly.Application.Common.Acp.Messages;

public sealed record PromptResult(string MessageId, string StopReason, AcpError? Error, UsageReport? Usage);
public sealed record PromptRun(Task<string> Inserted, Task<PromptResult> Ended);
public enum SteerOutcome { Injected, PromptRequired }
