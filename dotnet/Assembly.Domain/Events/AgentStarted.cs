namespace Assembly.Domain.Events;

/// <summary>
/// The adapter opened the session: a new one, or the session's earlier one, resumed. A resumed session's agent
/// reports its cost on from <c>SavedCost</c>, the total it reported at its last clean close, when that is known.
/// <c>Model</c> is the model its config options name, if they name one. <c>Folder</c> is the checkout or worktree the
/// session runs in, which a file tool's relative path is read against when the call names no folder of its own.
/// </summary>
public sealed record AgentStarted(long At, bool Resumed, decimal? SavedCost = null, string? Model = null, string? Folder = null) : AgentEvent(At);
