namespace Assembly.Application.Common.Acp.Messages;

public sealed record PromptResult(string MessageId, string StopReason, AcpError? Error, UsageReport? Usage);
public sealed record PromptRun(Task<string> Inserted, Task<PromptResult> Ended);
public enum SteerOutcome { Injected, PromptRequired }
public enum SteerRoute { None, SteeringCall, PromptQueue }
/// <summary>A message sent into a running turn. <see cref="Delivered"/> says whether the agent took it.</summary>
public sealed record SteerRun(string Id, Task<SteerOutcome> Delivered);
