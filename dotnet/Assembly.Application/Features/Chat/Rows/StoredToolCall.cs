namespace Assembly.Application.Features.Chat.Rows;

/// <summary>What an opened tool row shows: its summary (from its newest row) and its stored detail.</summary>
public sealed record StoredToolCall(string? Summary, string? Input, string? Output, string? Diff, string? Path);
