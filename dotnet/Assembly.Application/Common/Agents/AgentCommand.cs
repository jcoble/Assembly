namespace Assembly.Application.Common.Agents;

/// <summary>How to start an ACP adapter: the executable, its arguments, the directory to start it in, and environment variables added to ours.</summary>
public sealed record AgentCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string>? Environment = null);
