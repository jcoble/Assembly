using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Chat.Files;

public sealed record FileOwnership(string Machine, string CheckoutRoot, string RemoteSessionId);

/// <summary>Resolves trusted session metadata beside the files. Caller paths never select or authorize a root.</summary>
internal sealed class FileOwner(SessionStore sessions, IAppDatabase db)
{
    public FileOwnership Resolve(string sessionId, CancellationToken ct, FileOwnership? expected = null)
    {
        using var scope = db.OpenSnapshot(ct);
        var local = scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0))
            ?? throw new CallFailed("the owning engine identity is unavailable");
        var session = sessions.Get(scope, sessionId) ?? throw new CallFailed("unknown session");
        var engine = scope.QueryFirst("SELECT folder,holder,state FROM engine_session WHERE machine=$machine AND id=$id",
            r => new { Folder = r.Text(0), Holder = r.Text(1), State = r.Text(2) }, new("$machine", local), new("$id", sessionId));
        var remote = !string.IsNullOrEmpty(session.Machine) ? session.Machine :
            engine is { State: "foreign" } && engine.Holder != "local" ? engine.Holder : null;
        if (remote is null)
        {
            if (engine is not null && (engine.Holder != "local" || engine.State == "foreign"))
                throw new CallFailed("this session's checkout is not owned by this engine");
            var root = engine?.Folder ?? sessions.Root(scope, session) ?? throw new CallFailed("this session has no folder");
            var resolved = Match(new FileOwnership(local, root, sessionId), expected);
            if (!Directory.Exists(root)) throw new CallFailed("this session's folder is made on its first message");
            return resolved;
        }
        var owners = scope.Query("""
            SELECT machine FROM engine_mirror WHERE (machine=$remote OR name=$remote) AND state='live' LIMIT 2
            """, r => r.Text(0), new SqlArg("$remote", remote));
        if (owners.Count != 1) throw new CallFailed($"this session's files are on {remote}; the owning machine is disconnected or its identity is unavailable");
        var folder = scope.QueryFirst("""
            SELECT json_extract(r.row_json,'$.folder') FROM engine_mirror_row r JOIN engine_mirror m
              ON m.machine=r.machine AND m.generation=r.generation
            WHERE r.machine=$machine AND r.entity_kind='session' AND r.entity_id=$id AND m.state='live'
              AND json_extract(r.row_json,'$.holder')='local' AND json_extract(r.row_json,'$.state')<>'foreign'
            """, r => r.IsNull(0) ? null : r.Text(0), new("$machine", owners[0]), new("$id", sessionId));
        if (string.IsNullOrEmpty(folder)) throw new CallFailed("the owning machine has not published this session's checkout");
        return Match(new FileOwnership(owners[0], folder, sessionId), expected);
    }

    private static FileOwnership Match(FileOwnership actual, FileOwnership? expected)
    {
        if (expected is not null && expected != actual)
            throw new CallFailed("The session's checkout changed. The existing draft is preserved.", "checkout_changed");
        return actual;
    }

    public bool IsLocal(FileOwnership ownership, CancellationToken ct) => ownership.Machine ==
        db.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0), ct);
}
