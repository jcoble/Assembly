namespace Assembly.Domain.Rows;

/// <summary><c>Status</c> is <c>pending</c>, <c>in_progress</c> or <c>completed</c>.</summary>
public sealed record PlanItem(string Text, string Status);
