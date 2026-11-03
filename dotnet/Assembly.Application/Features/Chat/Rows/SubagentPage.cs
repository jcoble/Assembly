namespace Assembly.Application.Features.Chat.Rows;

/// <summary>A persisted native display card. SessionId retains its chat meaning, not an Engine identity.</summary>
public sealed record SubagentDisplay(string ItemId, long Seq, string SessionId, string Title, string? Description,
    string State, string? StopReason, string? Error, string? ToolCallId);

public sealed record SubagentPage(IReadOnlyList<SubagentDisplay> Items, string? Next)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxBytes = 1024 * 1024;
    public const int ItemMaxBytes = 32 * 1024;
    public const int PreviewBytes = 8 * 1024;
}
