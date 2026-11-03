using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Engine.Sessions;

// Internal validation handoff only. No public call or row stream is admitted by this descriptor.
internal sealed record SessionOpenKey(string Machine, string Id);
internal sealed record SessionOpenTarget(string Kind, string Machine, string Session,
    string? ItemId = null, long? Seq = null, string? PayloadSessionId = null,
    SessionOpenKey? Parent = null, ulong RequestId = 0);
internal sealed record SessionOpenTargetRead(string Kind, string Machine, long Generation, string? RouteMachine,
    string SessionId, string? ParentSessionId, string? ItemId, long? Seq, string? PayloadSessionId,
    bool ReadOnly, bool HasHistoryRows, SessionOpenKey OriginalKey, ulong RequestId, string? CheckoutRoot)
{
    public ExpectedSource ExpectedSource => new(Machine, Generation, RouteMachine);
    // Recorded metadata only: this neither authorizes filesystem access nor activates a workspace.
    public bool TranscriptOnly => string.IsNullOrWhiteSpace(CheckoutRoot);
}

/// <summary>Validates persisted display identity in one snapshot; never starts or invents a session.</summary>
internal sealed class SessionOpenTargets(IAppDatabase db, SessionStore sessions)
{
    public SessionOpenTargetRead Validate(SessionOpenTarget target, ExpectedSource source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(target.Session) || target.Machine != source.Machine ||
            target.Kind is not ("engine" or "native" or "historical")) throw Refused("bad_args");
        using var scope = db.OpenSnapshot(ct);
        var local = scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0));
        if (source.Machine == local)
        {
            if (source.Generation != 0 || source.RouteMachine is not null) throw Refused("source_changed");
        }
        else if (source.RouteMachine is not { Length: > 0 } || scope.QueryFirst("""
            SELECT COUNT(*) FROM engine_mirror
            WHERE machine=$machine AND generation=$generation AND name=$name AND state='live'
            """, row => row.Long(0), new SqlArg("$machine", source.Machine), new("$generation", source.Generation),
            new("$name", source.RouteMachine)) != 1) throw Refused("source_changed");
        // A copied facade names a saved route, not its originating Engine identity. Only the actual owner
        // can certify readable history; the pinned connection bridge must call this validator there.
        if (source.Machine != local) throw Refused("owner_validation_required");

        var session = sessions.Get(scope, target.Session) ?? throw Refused("unknown_session");
        if (session.Machine != source.RouteMachine) throw Refused("source_changed");
        var engineOwner = scope.QueryFirst("SELECT machine FROM engine_session WHERE id=$id",
            row => row.Text(0), new SqlArg("$id", target.Session));
        if (engineOwner is not null && engineOwner != source.Machine) throw Refused("source_changed");
        if (target.Kind == "native") return Native(scope, target, source, ct);
        if (target.ItemId is not null || target.Seq is not null || target.PayloadSessionId is not null || target.Parent is not null)
            throw Refused("bad_args");
        if (target.Kind == "engine")
        {
            var state = scope.QueryFirst("""
                SELECT state FROM engine_session WHERE id=$id AND machine=$machine AND holder='local' AND state<>'foreign'
                """, row => row.Text(0), new SqlArg("$id", target.Session), new("$machine", source.Machine));
            if (state is null) throw Refused("unknown_engine_session");
            if (state is "ended" or "lost" or "moved" or "handing_off") throw Refused("nonresumable");
        }
        ct.ThrowIfCancellationRequested();
        return new(target.Kind, source.Machine, source.Generation, source.RouteMachine, session.SessionId,
            session.ParentSessionId, null, null, null, target.Kind != "engine", HasRows(scope, session.SessionId),
            new(target.Machine, target.Session), target.RequestId, RecordedCheckout(session.CheckoutRoot));
    }

    private SessionOpenTargetRead Native(IReadScope scope, SessionOpenTarget target, ExpectedSource source, CancellationToken ct)
    {
        if (target.ItemId is not { Length: > 9 } item || !item.StartsWith("subagent:", StringComparison.Ordinal) ||
            target.Seq is not > 0 || string.IsNullOrWhiteSpace(target.PayloadSessionId) ||
            target.Parent != new SessionOpenKey(target.Machine, target.Session)) throw Refused("bad_args");
        var card = scope.QueryFirst("""
            SELECT seq,json_extract(payload,'$.sessionId'),
                CASE WHEN json_type(payload,'$.helper') IS NULL OR json_type(payload,'$.helper') IN ('null','false')
                    THEN 1 ELSE 0 END
            FROM rows WHERE session_id=$parent AND item_id=$item AND kind='subagent' ORDER BY seq DESC LIMIT 1
            """, row => (Seq: row.Long(0), Session: row.IsNull(1) ? null : row.Text(1), Native: row.Long(2) == 1),
            new SqlArg("$parent", target.Session), new("$item", item));
        if (card.Seq != target.Seq || card.Session != target.PayloadSessionId || !card.Native)
            throw Refused("native_card_changed");

        // The current native payload names its Assembly parent; older persisted cards may name their real child.
        // Only the persisted parent/ACP/source binding can identify readable child history, never that field alone.
        var nativeId = item["subagent:".Length..];
        var children = scope.Query("""
            SELECT session_id FROM session_info WHERE parent_session_id=$parent AND acp_session_id=$native
                AND machine IS $machine AND helper=0 ORDER BY session_id LIMIT 2
            """, row => row.Text(0), new SqlArg("$parent", target.Session), new("$native", nativeId),
            new("$machine", source.RouteMachine));
        if (children.Count != 1) throw Refused("native_history_unavailable");
        var child = children[0];
        if (card.Session != target.Session && card.Session != child) throw Refused("native_card_changed");
        if (!HasRows(scope, child)) throw Refused("native_history_unavailable");
        var childRecord = sessions.Get(scope, child) ?? throw Refused("native_history_unavailable");
        ct.ThrowIfCancellationRequested();
        return new("native", source.Machine, source.Generation, source.RouteMachine, child, target.Session,
            item, card.Seq, card.Session, true, true, new(target.Machine, target.Session), target.RequestId,
            RecordedCheckout(childRecord.CheckoutRoot));
    }

    private static string? RecordedCheckout(string? root) => string.IsNullOrWhiteSpace(root) ? null : root;

    private static bool HasRows(IReadScope scope, string session) => scope.QueryFirst(
        "SELECT EXISTS(SELECT 1 FROM rows WHERE session_id=$id)", row => row.Long(0) != 0, new SqlArg("$id", session));

    private static CallFailed Refused(string reason) => new("The selected session target is unavailable.", reason);
}
