using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>Files resources relayed for separate desktop sockets keep separate limits and close together.</summary>
internal sealed class FileRelayScopes(Action<string> release) : IDisposable
{
    private sealed class Scope(string id, string connection, long? replyId)
    {
        internal readonly string Id = id, Owner = connection, ResourceConnection = "files-relay:" + id;
        internal string? Attachment = Guid.NewGuid().ToString("N");
        internal readonly long? ReplyId = replyId;
        internal readonly HashSet<string> Connections = [connection];
        internal readonly CancellationTokenSource Stop = new();
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Scope> scopes = [];
    private bool disposed;

    internal FileScopeGrant Open(CallContext call)
    {
        RequireOwner(call);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            call.Closed.ThrowIfCancellationRequested();
            var id = Guid.NewGuid().ToString("N");
            var scope = new Scope(id, call.Connection, call.ReplyId);
            scopes.Add(id, scope);
            return new(id, scope.Attachment!);
        }
    }

    // Registered only on the bulk channel. The grant was issued to the authenticated normal connection.
    internal void Attach(string id, string attachment, CallContext call)
    {
        RequireOwner(call);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            call.Closed.ThrowIfCancellationRequested();
            if (!scopes.TryGetValue(id, out var scope) || scope.Attachment is null ||
                scope.Attachment != attachment || scope.Owner == call.Connection || scope.Connections.Count != 1)
                throw new CallFailed("The file relay attachment changed.", "files_scope_changed");
            scope.Connections.Add(call.Connection);
            scope.Attachment = null;
        }
    }

    internal BoundCall Bind(string id, CallContext call)
    {
        RequireOwner(call);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            call.Closed.ThrowIfCancellationRequested();
            if (!scopes.TryGetValue(id, out var scope) || scope.Attachment is not null ||
                !scope.Connections.Contains(call.Connection))
                throw new CallFailed("The file relay scope changed. Reopen the file.", "files_scope_changed");
            var closing = CancellationTokenSource.CreateLinkedTokenSource(call.Closed, scope.Stop.Token);
            var stop = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, closing.Token);
            return new BoundCall(call with
            {
                Connection = scope.ResourceConnection,
                Cancellation = stop.Token,
                Closed = closing.Token
            }, stop, closing);
        }
    }

    internal bool Release(string id, string connection)
    {
        Scope? scope;
        lock (gate)
        {
            if (!scopes.TryGetValue(id, out scope) || scope.Owner != connection) return false;
            scopes.Remove(id);
        }
        End(scope);
        return true;
    }

    internal void FailedOpen(CallContext call)
    {
        string? id;
        lock (gate) id = scopes.Values.FirstOrDefault(scope => scope.Owner == call.Connection && scope.ReplyId == call.ReplyId)?.Id;
        if (id is not null) Release(id, call.Connection);
    }

    internal void Close(string connection)
    {
        Scope[] ending;
        lock (gate)
        {
            ending = scopes.Values.Where(scope => scope.Connections.Contains(connection)).ToArray();
            foreach (var scope in ending) scopes.Remove(scope.Id);
        }
        foreach (var scope in ending) End(scope);
    }

    public void Dispose()
    {
        Scope[] ending;
        lock (gate)
        {
            disposed = true;
            ending = scopes.Values.ToArray();
            scopes.Clear();
        }
        foreach (var scope in ending) End(scope);
    }

    private void End(Scope scope)
    {
        try { scope.Stop.Cancel(); }
        finally
        {
            try { release(scope.ResourceConnection); }
            finally { scope.Stop.Dispose(); }
        }
    }

    private static void RequireOwner(CallContext call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (call.Peer is not OwnerPeer) throw new CallFailed("forbidden");
    }

    internal sealed class BoundCall(CallContext context, CancellationTokenSource stop, CancellationTokenSource closing) : IDisposable
    {
        internal CallContext Context { get; } = context;
        public void Dispose() { stop.Dispose(); closing.Dispose(); }
    }
}

internal sealed record FileScopeGrant(string Scope, string Attachment);
internal sealed record FileScopeOpen;
internal sealed record FileScopeAttach(string Scope, string Attachment);
internal sealed record FileScopeClose(string Scope);
