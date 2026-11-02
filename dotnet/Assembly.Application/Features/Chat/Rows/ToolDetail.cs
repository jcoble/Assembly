using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>
/// What an opened tool row shows beyond its one line. Null keeps what is already stored unless Replace requests a complete snapshot.
/// <c>Input</c> is the call's arguments as JSON text, and <c>Diff</c> is a unified diff.
/// </summary>
public sealed record ToolDetail(string ItemId, string? Input = null, string? Output = null, string? Diff = null, string? Path = null, bool Replace = false,
    IReadOnlyList<string>? TerminalIds = null, string? AcpSessionId = null, string? OutputSource = null);
