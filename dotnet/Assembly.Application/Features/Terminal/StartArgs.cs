namespace Assembly.Application.Features.Terminal;

/// <summary><c>terminal/start</c>: a blank shell or command is none; <paramref name="Owner"/> is the session that asked (terminal#9).</summary>
public sealed record StartArgs(string Cwd, string? Shell = null, string? Command = null, int? Cols = null, int? Rows = null,
    string? Owner = null);
