namespace Assembly.Application.Features.Terminal;

/// <summary>One terminal as the window lists it. <paramref name="Exit"/> is set once its process has ended (terminal#7).</summary>
public sealed record TerminalInfo(string Id, string Cwd, string Shell, int Cols, int Rows, int Pid, string? Owner,
    DateTimeOffset StartedAt, PtyExit? Exit);
