using Assembly.Engine.Contracts;

namespace Assembly.Engine.Sessions;

public sealed record ForeignAncestor(string SessionId, string Machine, string Provider, string Title, string? ParentId);
public sealed record ForeignAncestryArgs(string FromMachine, string ParentSessionId, IReadOnlyList<ForeignAncestor> RootToParent);
public sealed record EstablishForeignParent(string? ParentId) : SessionCommand;

/// <summary>Must escape the supplied writer so every batch write is rolled back together.</summary>
public sealed class ForeignAncestryRefused(CommandResult result) : Exception(result.Error)
{
    public CommandResult Result { get; } = result;
}
