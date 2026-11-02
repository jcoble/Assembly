namespace Assembly.Application.Features.Terminal;

/// <summary>What to start on the PTY. The environment overlays the backend's own unless <paramref name="ReplaceEnvironment"/>
/// is true; a null value removes the name.</summary>
public sealed record PtyRequest(string FileName, IReadOnlyList<string> Arguments, string Cwd,
    IReadOnlyDictionary<string, string?> Environment, int Cols, int Rows, bool ReplaceEnvironment = false);
