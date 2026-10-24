namespace Assembly.Engine.Acp.Messages;

/// <summary>Whether the agent stopped the task. Its end still arrives as an <c>async_task_state_update</c>.</summary>
public sealed record StopTaskResponse(bool Stopped);
