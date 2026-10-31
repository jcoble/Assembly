using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Workflows.Contracts;

namespace Assembly.Application.Features.Workflows;

/// <summary>Each read is one parameterized SQL statement, including its counts and continuations.</summary>
public sealed class WorkflowQueries(IAppDatabase db, string? remote = null)
{
    public BoardPageResult Page(BoardPageArgs args, CancellationToken cancel)
    {
        if (args.State is not null && !WorkflowTransitions.ValidState(args.State)) throw new CallFailed("invalid_argument");
        var c = Decode(args.Cursor, WorkflowSchema.Default.BoardCursor);
        if (c is not null && (c.AttentionGroup is < 0 or > 2 || c.MachineId is null || c.TaskKey is null))
            throw new CallFailed("invalid_argument");
        var result = db.QueryFirst(PageSql, r => JsonSerializer.Deserialize(r.Text(0), WorkflowSchema.Default.BoardPageResult)!, cancel,
            new SqlArg("$project", args.Project), new SqlArg("$state", args.State), new SqlArg("$cg", c?.AttentionGroup),
            new SqlArg("$cu", c?.UpdatedAt), new SqlArg("$cm", c?.MachineId), new SqlArg("$ck", c?.TaskKey), new SqlArg("$remote", remote))!;
        return result with { NextCursor = Encode(result.NextCursor) };
    }

    public BoardTaskResult Task(BoardTaskArgs args, CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(args.MachineId) || string.IsNullOrEmpty(args.TaskKey) || args.BeforeSeq is < 1)
            throw new CallFailed("invalid_argument");
        var c = Decode(args.QuestionCursor, WorkflowSchema.Default.BoardQuestionCursor);
        if (c is not null && c.QuestionKey is null) throw new CallFailed("invalid_argument");
        var result = db.QueryFirst(TaskSql, r => JsonSerializer.Deserialize(r.Text(0), WorkflowSchema.Default.BoardTaskResult)!, cancel,
            new SqlArg("$machine", args.MachineId), new SqlArg("$task", args.TaskKey), new SqlArg("$before", args.BeforeSeq),
            new SqlArg("$qa", c?.AskedAt), new SqlArg("$qk", c?.QuestionKey)) ?? throw new CallFailed("not_found");
        return result with { NextQuestionCursor = Encode(result.NextQuestionCursor) };
    }

    public BoardChangesResult Changes(BoardChangesArgs args, CancellationToken cancel)
    {
        if (args.AfterSeq < 0) throw new CallFailed("invalid_argument");
        return db.QueryFirst(ChangesSql, r => JsonSerializer.Deserialize(r.Text(0), WorkflowSchema.Default.BoardChangesResult)!, cancel,
            new SqlArg("$after", args.AfterSeq))!;
    }

    public long Applied(string machine, CancellationToken cancel) => db.QueryFirst(
        "SELECT COALESCE(MAX(seq),0) FROM board_events WHERE machine_id=$machine;", r => r.Long(0), cancel, new SqlArg("$machine", machine));

    private static T? Decode<T>(string? cursor, JsonTypeInfo<T> type) where T : class
    {
        if (cursor is null) return null;
        try
        {
            var raw = cursor.Replace('-', '+').Replace('_', '/');
            raw = raw.PadRight((raw.Length + 3) / 4 * 4, '=');
            return JsonSerializer.Deserialize(Convert.FromBase64String(raw), type) ?? throw new CallFailed("invalid_argument");
        }
        catch (Exception error) when (error is JsonException or FormatException) { throw new CallFailed("invalid_argument"); }
    }

    private static string? Encode(string? json) => json is null ? null :
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal const string PageSql = """
        WITH snapshots AS (
         SELECT e.machine_id,e.task_key,json_extract(e.payload_json,'$.steps') AS steps FROM board_events e
         WHERE e.seq=(SELECT MAX(seq) FROM board_events WHERE machine_id=e.machine_id AND task_key=e.task_key)
        ), filtered AS (
         SELECT t.machine_id,t.task_key,t.updated_at, json_object('machineId',t.machine_id,'taskKey',t.task_key,'creatorSessionId',t.creator_session_id,'project',t.project,'title',t.title,'summary',t.summary,'state',t.state,'taskUrl',t.task_url,'prUrl',t.pr_url,'startedAt',t.started_at,'endedAt',t.ended_at,'updatedAt',t.updated_at,'revision',t.revision) AS task, json_object( 'stepsDone',(SELECT CASE WHEN s.steps IS NOT NULL THEN COUNT(*) END FROM json_each(s.steps) WHERE json_extract(value,'$.state')='done'),
            'stepsFailed',(SELECT CASE WHEN s.steps IS NOT NULL THEN COUNT(*) END FROM json_each(s.steps) WHERE json_extract(value,'$.state')='failed'),
            'stepsSkipped',(SELECT CASE WHEN s.steps IS NOT NULL THEN COUNT(*) END FROM json_each(s.steps) WHERE json_extract(value,'$.state')='skipped'),'tools',NULL,'files',
            CASE WHEN EXISTS(SELECT 1 FROM board_events e WHERE e.machine_id=t.machine_id AND e.task_key=t.task_key
              AND json_type(e.payload_json,'$.changedPaths')='array')
            THEN (SELECT COUNT(DISTINCT p.value) FROM board_events e, json_each(e.payload_json,'$.changedPaths') p
              WHERE e.machine_id=t.machine_id AND e.task_key=t.task_key) ELSE NULL END) AS metrics,
         (SELECT COUNT(*) FROM board_questions q WHERE q.machine_id=t.machine_id AND q.task_key=t.task_key AND q.answer_json IS NULL) AS questions,
         CASE WHEN EXISTS(SELECT 1 FROM board_questions q WHERE q.machine_id=t.machine_id AND q.task_key=t.task_key AND q.answer_json IS NULL)
         THEN 0 WHEN t.state IN ('completed','failed','cancelled') THEN 2 ELSE 1 END AS g
         FROM board_tasks t LEFT JOIN snapshots s ON s.machine_id=t.machine_id AND s.task_key=t.task_key WHERE ($project IS NULL OR t.project=$project) AND ($state IS NULL OR t.state=$state)
        ), paged AS (
         SELECT task,metrics,questions,g,updated_at,machine_id,task_key FROM filtered WHERE $cg IS NULL OR g>$cg OR (g=$cg AND
         (updated_at<$cu OR (updated_at=$cu AND (machine_id>$cm COLLATE BINARY OR (machine_id=$cm AND task_key>$ck COLLATE BINARY)))))
         ORDER BY g,updated_at DESC,machine_id COLLATE BINARY,task_key COLLATE BINARY LIMIT 41
        ), shown AS (SELECT task,metrics,questions,g,updated_at,machine_id,task_key FROM paged LIMIT 40)
        SELECT json_object(
         'items',json((SELECT json_group_array(json_object('task',json(task),'attentionGroup',g,'questionCount',questions,'metrics',json(metrics))) FROM shown)),
         'counts',json_object('total',(SELECT COUNT(*) FROM filtered),'running',(SELECT COUNT(*) FROM filtered WHERE g=1),
         'needsYou',(SELECT COUNT(*) FROM filtered WHERE g=0),'terminal',(SELECT COUNT(*) FROM filtered WHERE g=2)),
         'nextCursor',CASE WHEN (SELECT COUNT(*) FROM paged)>40 THEN
         (SELECT json_object('attentionGroup',g,'updatedAt',updated_at,'machineId',machine_id,'taskKey',task_key) FROM shown LIMIT 1 OFFSET 39)||'' ELSE NULL END,
         'watermarks',json((SELECT json_group_array(json_object('machineId',machine_id,'seq',seq)) FROM
         (SELECT machine_id,MAX(seq) AS seq FROM board_events GROUP BY machine_id
         UNION ALL SELECT 'local',0 WHERE NOT EXISTS(SELECT 1 FROM board_events WHERE machine_id='local')
         UNION ALL SELECT $remote,0 WHERE $remote IS NOT NULL AND NOT EXISTS(SELECT 1 FROM board_events WHERE machine_id=$remote)))));
        """;

    internal const string TaskSql = """
        WITH snapshot AS (
         SELECT json_extract(payload_json,'$.steps') AS steps FROM board_events
         WHERE machine_id=$machine AND task_key=$task ORDER BY seq DESC LIMIT 1
        ), questions AS (
         SELECT q.asked_at,q.question_key, json_object('machineId',q.machine_id,'taskKey',q.task_key,'questionKey',q.question_key,'generation',q.generation,'kind',q.kind,'prompt',q.prompt,'recommendation',q.recommendation,'askedAt',q.asked_at,'answer',json(q.answer_json),'answeredAt',q.answered_at,'answerKey',q.answer_key,'deliveryClientId',q.delivery_client_id,'deliveryTurnId',q.delivery_turn_id,'deliveryState',q.delivery_state,'acceptedRevision',q.accepted_revision) AS item FROM board_questions q
         WHERE q.machine_id=$machine AND q.task_key=$task AND
         ($qa IS NULL OR asked_at>$qa OR (asked_at=$qa AND question_key>$qk COLLATE BINARY))
         ORDER BY asked_at,question_key COLLATE BINARY LIMIT 51
        ), shown AS (SELECT asked_at,question_key,item FROM questions LIMIT 50),
         feed AS (
         SELECT e.seq,json_set(json_remove(e.payload_json,'$.steps'),'$.machineId',e.machine_id,'$.seq',e.seq,'$.taskKey',e.task_key,'$.kind',e.kind,'$.occurredAt',e.occurred_at) AS item FROM board_events e WHERE e.machine_id=$machine AND e.task_key=$task AND
         ($before IS NULL OR e.seq<$before) ORDER BY seq DESC LIMIT 101
        ), visible AS (SELECT seq,item FROM feed LIMIT 100)
        SELECT json_object('task',json(json_object('machineId',t.machine_id,'taskKey',t.task_key,'creatorSessionId',t.creator_session_id,'project',t.project,'title',t.title,'summary',t.summary,'state',t.state,'taskUrl',t.task_url,'prUrl',t.pr_url,'startedAt',t.started_at,'endedAt',t.ended_at,'updatedAt',t.updated_at,'revision',t.revision)),'questions',json((SELECT json_group_array(json(item)) FROM shown)),
         'steps',json(COALESCE(s.steps,'[]')),
         'feed',json((SELECT json_group_array(json(item)) FROM visible)),'metrics',json(json_object( 'stepsDone',(SELECT CASE WHEN s.steps IS NOT NULL THEN COUNT(*) END FROM json_each(s.steps) WHERE json_extract(value,'$.state')='done'),
            'stepsFailed',(SELECT CASE WHEN s.steps IS NOT NULL THEN COUNT(*) END FROM json_each(s.steps) WHERE json_extract(value,'$.state')='failed'),
            'stepsSkipped',(SELECT CASE WHEN s.steps IS NOT NULL THEN COUNT(*) END FROM json_each(s.steps) WHERE json_extract(value,'$.state')='skipped'),'tools',NULL,'files',
            CASE WHEN EXISTS(SELECT 1 FROM board_events e WHERE e.machine_id=t.machine_id AND e.task_key=t.task_key
              AND json_type(e.payload_json,'$.changedPaths')='array')
            THEN (SELECT COUNT(DISTINCT p.value) FROM board_events e, json_each(e.payload_json,'$.changedPaths') p
              WHERE e.machine_id=t.machine_id AND e.task_key=t.task_key) ELSE NULL END)),
         'nextBeforeSeq',CASE WHEN (SELECT COUNT(*) FROM feed)>100 THEN (SELECT MIN(seq) FROM visible) ELSE NULL END,
         'nextQuestionCursor',CASE WHEN (SELECT COUNT(*) FROM questions)>50 THEN
         (SELECT json_object('askedAt',asked_at,'questionKey',question_key) FROM shown LIMIT 1 OFFSET 49)||'' ELSE NULL END)
         FROM board_tasks t LEFT JOIN snapshot s ON 1=1 WHERE t.machine_id=$machine AND t.task_key=$task;
        """;

    internal const string ChangesSql = """
        WITH page AS (SELECT e.seq,json_set(e.payload_json,'$.machineId',e.machine_id,'$.seq',e.seq,'$.taskKey',e.task_key,'$.kind',e.kind,'$.occurredAt',e.occurred_at) AS item FROM board_events e
         WHERE e.machine_id='local' AND seq>$after ORDER BY seq LIMIT 100)
        SELECT json_object('events',json((SELECT json_group_array(json(item)) FROM page)),
         'nextSeq',COALESCE((SELECT MAX(seq) FROM page),$after),
         'latestSeq',COALESCE((SELECT MAX(seq) FROM board_events WHERE machine_id='local'),0));
        """;

    internal const string TaskRow = "json_object('machineId',t.machine_id,'taskKey',t.task_key,'creatorSessionId',t.creator_session_id,'project',t.project,'title',t.title,'summary',t.summary,'state',t.state,'taskUrl',t.task_url,'prUrl',t.pr_url,'startedAt',t.started_at,'endedAt',t.ended_at,'updatedAt',t.updated_at,'revision',t.revision)";
    internal const string QuestionRow = "json_object('machineId',q.machine_id,'taskKey',q.task_key,'questionKey',q.question_key,'generation',q.generation,'kind',q.kind,'prompt',q.prompt,'recommendation',q.recommendation,'askedAt',q.asked_at,'answer',json(q.answer_json),'answeredAt',q.answered_at,'answerKey',q.answer_key,'deliveryClientId',q.delivery_client_id,'deliveryTurnId',q.delivery_turn_id,'deliveryState',q.delivery_state,'acceptedRevision',q.accepted_revision)";
    internal const string EventRow = "json_set(e.payload_json,'$.machineId',e.machine_id,'$.seq',e.seq,'$.taskKey',e.task_key,'$.kind',e.kind,'$.occurredAt',e.occurred_at)";
}
