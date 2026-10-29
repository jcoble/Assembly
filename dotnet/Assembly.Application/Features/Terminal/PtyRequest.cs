namespace Assembly.Application.Features.Terminal;

/// <summary>What to start on the PTY. <paramref name="Environment"/> is added to the backend's own; a null value removes the name.</summary>
public sealed record PtyRequest(string FileName, IReadOnlyList<string> Arguments, string Cwd,
    IReadOnlyDictionary<string, string?> Environment, int Cols, int Rows);
