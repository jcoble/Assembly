using Assembly.Domain.Events;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>What an opened tool row shows: its summary (from its newest row) and its stored detail.</summary>
public sealed record StoredToolCall(string? Summary, string? Input, string? Output, string? Diff, string? Path,
    long? OutputBytes = null, bool OutputTruncated = false, IReadOnlyList<ToolTerminalState>? Terminals = null);

public sealed record ToolTerminalState(string AcpSessionId, string TerminalId, string? Command, string? Cwd, TerminalExit? ExitStatus);
