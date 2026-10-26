namespace Assembly.Domain.Rows;

/// <summary>The thinking so far, whole. <c>Done</c> once it has ended.</summary>
public sealed record ThinkingPayload(string Text, bool Done);
