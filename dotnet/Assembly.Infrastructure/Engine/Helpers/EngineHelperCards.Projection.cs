using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Domain.Sessions;

namespace Assembly.Infrastructure.Engine.Helpers;

public sealed partial class EngineHelperCards
{
    private sealed record Card(string Id, string ParentId, string Source, long Generation, long Epoch,
        long Version, long TerminalSeq, SessionInfo Session, SubagentPayload Payload, SubagentPayload? Previous)
    {
        public bool OlderThan(Card other) => Source == other.Source &&
            (Generation < other.Generation || Generation == other.Generation &&
                (Epoch < other.Epoch || Epoch == other.Epoch &&
                    (Version < other.Version || Version == other.Version && TerminalSeq < other.TerminalSeq)));
    }

    private static IReadOnlyList<Card> Read(IReadScope scope, string? childId = null, string? source = null) => scope.Query("""
        WITH children AS (
            SELECT c.id,c.parent_id,c.provider,CAST(c.title AS BLOB) title,c.state,c.activity_state,
                c.epoch,c.version,0 generation,c.machine,
                EXISTS(SELECT 1 FROM engine_message m WHERE m.to_session_id=c.id
                    AND m.state='queued' AND m.in_flight=1) prompt_in_flight,
                c.folder,c.updated_ms,NULL display_machine,pi.repo_url
            FROM engine_session c JOIN engine_session p ON p.id=c.parent_id
            JOIN session_info pi ON pi.session_id=p.id AND pi.machine IS NULL
            WHERE c.machine=(SELECT machine_id FROM engine_meta WHERE singleton=1) AND c.holder='local'
                AND c.state<>'foreign' AND p.machine=c.machine AND p.holder='local' AND p.state<>'foreign'
                AND ($id IS NULL OR c.id=$id) AND ($source IS NULL OR c.machine=$source)
            UNION ALL
            SELECT r.entity_id,json_extract(r.row_json,'$.parentId'),json_extract(r.row_json,'$.provider'),
                CAST(CASE WHEN json_extract(r.row_json,'$.titleComplete') IS NOT 0
                    THEN json_extract(r.row_json,'$.title') ELSE si.title END AS BLOB),
                json_extract(r.row_json,'$.state'),json_extract(r.row_json,'$.activityState'),
                json_extract(r.row_json,'$.epoch'),r.version,r.generation,r.machine,
                EXISTS(SELECT 1 FROM engine_mirror_row message
                    WHERE message.machine=r.machine AND message.generation=r.generation AND message.entity_kind='message'
                        AND json_extract(message.row_json,'$.toSessionId')=r.entity_id
                        AND json_extract(message.row_json,'$.state')='queued'
                        AND json_extract(message.row_json,'$.inFlight')=1),
                json_extract(r.row_json,'$.folder'),json_extract(r.row_json,'$.updatedMs'),m.name,pi.repo_url
            FROM engine_mirror_row r JOIN engine_mirror m ON m.machine=r.machine AND m.generation=r.generation AND m.state='live'
            JOIN engine_session p ON p.id=json_extract(r.row_json,'$.parentId')
                AND p.machine=(SELECT machine_id FROM engine_meta WHERE singleton=1) AND p.holder='local' AND p.state<>'foreign'
            JOIN session_info pi ON pi.session_id=p.id AND pi.machine IS NULL
            LEFT JOIN session_info si ON si.session_id=r.entity_id AND si.machine=m.name
            WHERE r.entity_kind='session' AND json_extract(r.row_json,'$.holder')='local'
                AND json_extract(r.row_json,'$.machine')=r.machine AND json_extract(r.row_json,'$.id')=r.entity_id
                AND json_extract(r.row_json,'$.state')<>'foreign'
                AND ($id IS NULL OR r.entity_id=$id) AND ($source IS NULL OR r.machine=$source)
        )
        SELECT c.id,c.parent_id,c.provider,c.title,c.state,c.activity_state,c.epoch,c.version,c.generation,c.machine,
            t.seq,json_extract(t.payload,'$.state'),
            (SELECT json_extract(e.payload,'$.message') FROM rows e WHERE e.session_id=c.id AND e.kind='error'
                AND e.run_id IS t.run_id AND e.seq<t.seq ORDER BY e.seq DESC LIMIT 1),
            (SELECT payload FROM rows old WHERE old.session_id=c.parent_id AND old.kind='subagent'
                AND old.item_id='subagent:'||c.id ORDER BY old.seq DESC LIMIT 1),c.prompt_in_flight,
            c.folder,c.updated_ms,c.display_machine,c.repo_url
        FROM children c LEFT JOIN rows t ON t.session_id=c.id AND t.seq=(
            SELECT seq FROM rows WHERE session_id=c.id AND kind='turn' ORDER BY seq DESC LIMIT 1)
        WHERE c.title IS NOT NULL
        """, r =>
    {
        var id = r.Text(0); var parent = r.Text(1); var agent = r.Text(2);
        var title = new UTF8Encoding(false, true).GetString(r.Blob(3));
        var state = r.Text(4); var activity = r.Text(5);
        // Starting the adapter releases its first prompt before the provider reports TurnStarted.
        var running = state is "starting" or "running" || state == "needs_me" && activity == "running" ||
            state == "idle" && r.Long(14) != 0;
        var terminal = r.IsNull(11) ? null : r.Text(11);
        var stop = !running && terminal == "cancelled" ? "cancelled" : null;
        var error = !running && terminal == "failed" ? r.IsNull(12) ? "It failed." : r.Text(12) : null;
        var session = new SessionInfo(id, agent, r.Text(18), title, null, r.Long(16),
            Machine: r.IsNull(17) ? null : r.Text(17), ParentSessionId: parent, Helper: true, WorkingFolder: r.Text(15));
        var payload = new SubagentPayload(id, title, $"Session ({agent})", running ? "running" : "idle", stop, error,
            CanStop: running, ToolCallId: null, Agent: agent, Helper: true);
        return new Card(id, parent, r.Text(9), r.Long(8), r.Long(6), r.Long(7), r.IsNull(10) ? 0 : r.Long(10),
            session, payload, r.IsNull(13) ? null : JsonSerializer.Deserialize(r.Text(13), RowJsonContext.Rows.SubagentPayload));
    }, new SqlArg("$id", childId), new SqlArg("$source", source));
}
