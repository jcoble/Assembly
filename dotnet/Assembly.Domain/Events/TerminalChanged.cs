namespace Assembly.Domain.Events;

public sealed record TerminalExit(long? ExitCode, string? Signal);

/// <summary>A terminal's byte delta or replacement, distinct from tool-call status.</summary>
public sealed record TerminalChanged(
    long At, string TerminalId,
    string? Data = null, bool OutputReplaced = false,
    bool CommandReplaced = false, string? Command = null,
    bool CwdReplaced = false, string? Cwd = null,
    bool ExitStatusReplaced = false, TerminalExit? ExitStatus = null,
    string? AcpSessionId = null) : AgentEvent(At);
