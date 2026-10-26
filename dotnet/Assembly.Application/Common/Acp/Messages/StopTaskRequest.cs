namespace Assembly.Application.Common.Acp.Messages;

/// <summary>Stops one background task (<c>_session/async_task/stop</c>), one the agent said <c>canStop</c> of.</summary>
public sealed record StopTaskRequest(string SessionId, string AsyncTaskId);
