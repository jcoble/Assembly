namespace Assembly.Engine.Rows;

/// <summary>The row kinds, as the UI knows them.</summary>
public static class RowKind
{
    public const string Turn = "turn";
    public const string User = "user";
    public const string Delta = "delta";
    public const string Message = "message";
    public const string Thinking = "thinking";
    public const string Tool = "tool";
    public const string Plan = "plan";
    public const string Stats = "stats";
    public const string Error = "error";
    public const string Ask = "ask";
    public const string Task = "task";
    public const string Context = "context";
    public const string Subagent = "subagent";
}
