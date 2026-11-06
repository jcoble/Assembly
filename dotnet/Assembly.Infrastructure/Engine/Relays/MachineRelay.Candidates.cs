using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine.Relays;

internal sealed partial class MachineRelay
{
    private sealed record Candidate(string Source, string Target, string Message, string From, string To, long Created);
    private IReadOnlyList<Candidate> Candidates(IReadScope scope, IReadOnlyDictionary<string, Endpoint> endpoints, Candidate? after)
    {
        IReadOnlyList<MachineStartMirrorGeneration> eligible = endpoints.Values.Select(owner => new MachineStartMirrorGeneration(owner.Machine, owner.Live!.Generation)).ToArray();
        var owners = JsonSerializer.Serialize(eligible, EngineJson.Default.IReadOnlyListMachineStartMirrorGeneration);
        return scope.Query("""
            WITH eligible AS (
                SELECT m.machine,m.generation FROM json_each($owners) o JOIN engine_mirror m
                    ON m.machine=json_extract(o.value,'$.machine') AND m.generation=json_extract(o.value,'$.generation') AND m.state='live'
            ), sessions AS (
                SELECT id,machine FROM engine_session WHERE machine=$local AND state<>'foreign'
                UNION ALL
                SELECT r.entity_id,r.machine FROM engine_mirror_row r JOIN eligible e ON e.machine=r.machine AND e.generation=r.generation
                    WHERE r.entity_kind='session' AND json_extract(r.row_json,'$.machine')=r.machine AND json_extract(r.row_json,'$.state')<>'foreign'
            ), owners AS (
                SELECT id,MIN(machine) AS machine FROM sessions GROUP BY id HAVING COUNT(*)=1
            ), queued AS (
                SELECT m.machine AS source,t.machine AS target,m.id AS message,m.from_session_id AS sender,m.to_session_id AS recipient,m.created_ms AS created
                FROM engine_message m JOIN engine_session t ON t.id=m.to_session_id
                JOIN owners o ON o.id=t.id AND o.machine=t.machine
                WHERE m.machine=$local AND t.state='foreign' AND t.machine<>$local AND m.state='queued' AND m.in_flight=0 AND m.framed=1
                    AND m.sender_kind IN ('session','system') AND m.from_session_id IS NOT NULL
                UNION ALL
                SELECT r.machine,o.machine,r.entity_id,json_extract(r.row_json,'$.fromSessionId'),json_extract(r.row_json,'$.toSessionId'),json_extract(r.row_json,'$.createdMs')
                FROM engine_mirror_row r JOIN eligible e ON e.machine=r.machine AND e.generation=r.generation
                JOIN owners o ON o.id=json_extract(r.row_json,'$.toSessionId') AND o.machine<>r.machine
                WHERE r.entity_kind='message' AND json_extract(r.row_json,'$.state')='queued' AND json_extract(r.row_json,'$.framed')=1
                    AND json_extract(r.row_json,'$.machine')=r.machine AND json_extract(r.row_json,'$.senderKind') IN ('session','system')
                    AND json_extract(r.row_json,'$.fromSessionId') IS NOT NULL
            ), ranked AS (
                SELECT source,target,message,sender,recipient,created,ROW_NUMBER() OVER(PARTITION BY source,sender,recipient ORDER BY created,message) AS position FROM queued
            ) SELECT source,target,message,sender,recipient,created FROM ranked WHERE position=1
                AND ($afterCreated IS NULL OR created>$afterCreated OR created=$afterCreated AND (source>$afterSource OR source=$afterSource AND message>$afterMessage))
                ORDER BY created,source,message LIMIT 200
            """, row => new Candidate(row.Text(0), row.Text(1), row.Text(2), row.Text(3), row.Text(4), row.Long(5)),
            new SqlArg("$owners", owners), new SqlArg("$local", _runtime.Machine), new SqlArg("$afterCreated", after?.Created),
            new SqlArg("$afterSource", after?.Source), new SqlArg("$afterMessage", after?.Message));
    }
}
