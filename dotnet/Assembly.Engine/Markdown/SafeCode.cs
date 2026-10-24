namespace Assembly.Engine.Markdown;

/// <summary>A code block. <c>Complete</c> is false while a fenced block is still streaming (no closing fence yet).</summary>
public sealed record SafeCode(string Language, string Value, bool Complete) : SafeBlock;
