namespace Assembly.Application.Features.Terminal;

/// <summary>How a terminal's process ended: its exit code, or the signal that killed it.</summary>
public sealed record PtyExit(int? Code, int? Signal);
