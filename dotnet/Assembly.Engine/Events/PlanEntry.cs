namespace Assembly.Engine.Events;

/// <summary>One step of a plan. <c>Status</c> is <c>pending</c>, <c>in_progress</c> or <c>completed</c>.</summary>
public sealed record PlanEntry(string Text, string Status);
