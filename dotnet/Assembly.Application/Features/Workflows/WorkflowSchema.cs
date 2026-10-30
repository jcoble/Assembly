using System.Text.Json.Serialization;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Workflows.Contracts;

namespace Assembly.Application.Features.Workflows;

// Application persistence needs its own generated metadata; Infrastructure registers the same wire types.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(BoardAnswerArgs))]
[JsonSerializable(typeof(BoardAnswerReceived))]
[JsonSerializable(typeof(BoardAnswerResult))]
[JsonSerializable(typeof(BoardAnswerValue))]
[JsonSerializable(typeof(BoardChange))]
[JsonSerializable(typeof(BoardChangesArgs))]
[JsonSerializable(typeof(BoardChangesResult))]
[JsonSerializable(typeof(BoardCounts))]
[JsonSerializable(typeof(BoardCursor))]
[JsonSerializable(typeof(BoardEvent))]
[JsonSerializable(typeof(BoardStep))]
[JsonSerializable(typeof(BoardMetrics))]
[JsonSerializable(typeof(BoardPageArgs))]
[JsonSerializable(typeof(BoardPageResult))]
[JsonSerializable(typeof(BoardQuestion))]
[JsonSerializable(typeof(BoardQuestionAsk))]
[JsonSerializable(typeof(BoardQuestionCursor))]
[JsonSerializable(typeof(BoardSummary))]
[JsonSerializable(typeof(BoardTask))]
[JsonSerializable(typeof(BoardTaskArgs))]
[JsonSerializable(typeof(BoardTaskResult))]
[JsonSerializable(typeof(BoardUpdateArgs))]
[JsonSerializable(typeof(BoardUpdateResult))]
[JsonSerializable(typeof(BoardWatermark))]
public partial class WorkflowSchema : JsonSerializerContext
{
    public static void Open(IAppDatabase db) => db.Writer.Write("board.schema", s => s.Execute(Create));

    internal const string States = "'queued','running','paused','waiting','completed','failed','cancelled'";
    internal const string Create = """
        CREATE TABLE IF NOT EXISTS board_tasks(
          machine_id TEXT NOT NULL, task_key TEXT NOT NULL, creator_session_id TEXT NOT NULL,
          project TEXT NOT NULL, title TEXT NOT NULL, summary TEXT NOT NULL, state TEXT NOT NULL
          CHECK(state IN ('queued','running','paused','waiting','completed','failed','cancelled')),
          task_url TEXT, pr_url TEXT, started_at INTEGER NOT NULL, ended_at INTEGER,
          updated_at INTEGER NOT NULL, revision INTEGER NOT NULL, PRIMARY KEY(machine_id,task_key));
        CREATE TABLE IF NOT EXISTS board_questions(
          machine_id TEXT NOT NULL, task_key TEXT NOT NULL, question_key TEXT NOT NULL,
          generation INTEGER NOT NULL, kind TEXT NOT NULL CHECK(kind IN ('text','approval')), prompt TEXT NOT NULL,
          recommendation TEXT, asked_at INTEGER NOT NULL, answer_json TEXT, answered_at INTEGER,
          answer_key TEXT, delivery_client_id TEXT, delivery_turn_id TEXT, delivery_state TEXT NOT NULL,
          accepted_revision INTEGER, PRIMARY KEY(machine_id,task_key,question_key), UNIQUE(machine_id,answer_key),
          FOREIGN KEY(machine_id,task_key) REFERENCES board_tasks(machine_id,task_key));
        CREATE TABLE IF NOT EXISTS board_events(
          machine_id TEXT NOT NULL, seq INTEGER NOT NULL, task_key TEXT NOT NULL, kind TEXT NOT NULL,
          occurred_at INTEGER NOT NULL, payload_json TEXT NOT NULL, PRIMARY KEY(machine_id,seq),
          FOREIGN KEY(machine_id,task_key) REFERENCES board_tasks(machine_id,task_key));
        CREATE INDEX IF NOT EXISTS board_tasks_filter ON board_tasks(project,state,updated_at DESC,machine_id,task_key);
        CREATE INDEX IF NOT EXISTS board_tasks_order ON board_tasks(updated_at DESC,machine_id,task_key);
        CREATE INDEX IF NOT EXISTS board_questions_task ON board_questions(machine_id,task_key,answered_at,question_key);
        CREATE INDEX IF NOT EXISTS board_events_task ON board_events(machine_id,task_key,seq DESC);
        CREATE UNIQUE INDEX IF NOT EXISTS board_answer_identity ON board_events(
          machine_id,json_extract(payload_json,'$.questions[0].answerKey')) WHERE kind='answer_recorded';
        """;
}
