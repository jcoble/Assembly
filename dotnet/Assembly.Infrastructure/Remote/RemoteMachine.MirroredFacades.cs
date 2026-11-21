using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    /// <summary>
    /// Records, in the caller's write, the facade of a session this machine's live mirror carries, with its mirrored
    /// ancestors: their owner records (the foreign ancestry a created session's acceptance establishes) and the rows
    /// naming the route. A session started on the remote, by its workflows, its phone or another desktop is then read,
    /// saved and sent to as a created one is. Returns false, writing nothing, when the owner can't be proven from what
    /// this machine knows; a facade already naming another engine is never changed, so it keeps an unknown owner.
    /// </summary>
    internal bool RecordMirroredFacade(IWriteScope scope, string machine, string sessionId)
    {
        var engine = RequireAcceptanceEngine();
        if (_sessionStore.MirroredChain(scope, machine, _name, sessionId) is not { } chain) return false;
        scope.Execute("SAVEPOINT mirrored_facade");
        try
        {
            // The chain's outside parent, when it has one, is already known here, as a created child's is.
            var prefix = chain[0].ParentId is { } parent ? engine.Commands.ReadKnownAncestry(scope, parent) : Array.Empty<ForeignAncestor>();
            if (prefix is null) throw AcceptedSourceChanged();
            IReadOnlyList<ForeignAncestor> nodes = [.. prefix, .. chain.Select(session =>
                new ForeignAncestor(session.SessionId, machine, session.Facts.Agent, session.Title, session.ParentId))];
            var prepared = engine.Commands.PrepareForeignAncestry(new(machine, sessionId, nodes));
            if (!engine.Commands.ApplyForeignAncestryBatch(scope, prepared).Applied) throw AcceptedSourceChanged();
            foreach (var session in chain) _sessionStore.RecordCopied(scope, session.SessionId, session.Facts, _name, null);
            scope.Execute("RELEASE SAVEPOINT mirrored_facade");
            return true;
        }
        catch (Exception error) when (error is ForeignAncestryRefused or CallFailed)
        {
            scope.Execute("ROLLBACK TO SAVEPOINT mirrored_facade");
            scope.Execute("RELEASE SAVEPOINT mirrored_facade");
            return false;
        }
    }
}
