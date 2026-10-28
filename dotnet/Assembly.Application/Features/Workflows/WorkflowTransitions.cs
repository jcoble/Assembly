using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Workflows.Contracts;

namespace Assembly.Application.Features.Workflows;

/// <summary>Board source transitions and complete remote event copies, each committed before publication.</summary>
public sealed class WorkflowTransitions(IAppDatabase db, TimeProvider time)
{
    public static bool ValidState(string? value) => value is "queued" or "running" or "paused" or "waiting" or "completed" or "failed" or "cancelled";
    private static bool Terminal(string state) => state is "completed" or "failed" or "cancelled";
    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();
    private static void Text(string? value, int max, bool required = false)
    {
        if (value is null ? required : value.Length > max || (required && string.IsNullOrWhiteSpace(value)))
            throw new CallFailed("invalid_argument");
    }

    public BoardUpdateResult Update(string creator, string project, BoardUpdateArgs args)
    {
        Text(args.TaskKey, 80, true); Text(args.Title, 120, true); Text(args.Summary, 600);
        if (args.Summary is null) throw new CallFailed("invalid_argument");
        Text(args.TaskUrl, 2048); Text(args.PrUrl, 2048);
        if (!ValidState(args.State) || args.Question is not null && args.AnswerReceived is not null ||
            args.ChangedPaths is { Length: > 32 }) throw new CallFailed("invalid_argument");
        if (args.ChangedPaths is { } paths) foreach (var path in paths) Text(path, 240, true);
        if (args.Question is { } ask)
        {
            Text(ask.QuestionKey, 80, true); Text(ask.Prompt, 1200, true); Text(ask.Recommendation, 300);
            if (ask.ExpectedGeneration is < 0 or int.MaxValue || ask.Kind is not ("text" or "approval")) throw new CallFailed("invalid_argument");
        }
        return db.Writer.Write("board.report", scope =>
        {
            var task = ReadTask(scope, "local", args.TaskKey);
            if (task is not null && task.CreatorSessionId != creator) throw new CallFailed("not_owner");
            var now = Now;
            var next = task is null
                ? new BoardTask("local", args.TaskKey, creator, project, args.Title, args.Summary, args.State,
                    args.TaskUrl, args.PrUrl, now, Terminal(args.State) ? now : null, now, 0)
                : task with
                {
                    Title = args.Title,
                    Summary = args.Summary,
                    State = args.State,
                    TaskUrl = args.TaskUrl,
                    PrUrl = args.PrUrl,
                    EndedAt = Terminal(args.State) ? task.EndedAt ?? now : null
                };
            BoardQuestion? question = null;
            var kind = "task_reported";
            if (args.AnswerReceived is { } received)
            {
                if (task is null) throw new CallFailed("not_found");
                var a = new BoardAnswerArgs("local", args.TaskKey, received.QuestionKey, received.Generation, received.AnswerKey, received.Answer);
                ValidateAnswer(a);
                if (Previous(scope, a) is { } previous)
                    return new BoardUpdateResult("local", args.TaskKey, previous.AcceptedRevision, previous.QuestionKey, previous.Generation, previous);
                question = Accept(scope, task, a, true, now);
                kind = "answer_recorded";
            }
            else if (args.Question is { } requested)
            {
                var old = ReadQuestion(scope, "local", args.TaskKey, requested.QuestionKey);
                if (old?.DeliveryState == "pending") throw new CallFailed("already_answered");
                // A retry carries the generation preceding the still-unanswered ask.
                if (old is { Answer: null } && old.Generation == requested.ExpectedGeneration + 1 &&
                    old.Kind == requested.Kind && old.Prompt == requested.Prompt && old.Recommendation == requested.Recommendation)
                {
                    if (next == task && args.ChangedPaths is null)
                        return new BoardUpdateResult("local", args.TaskKey, task!.Revision, old.QuestionKey, old.Generation, null);
                }
                else
                {
                    if ((old?.Generation ?? 0) != requested.ExpectedGeneration) throw new CallFailed("stale_generation");
                    question = new("local", args.TaskKey, requested.QuestionKey, checked(requested.ExpectedGeneration + 1), requested.Kind,
                        requested.Prompt, requested.Recommendation, now, null, null, null, null, null, "none", null);
                    kind = "question_asked";
                }
            }
            if (task is not null && next == task && question is null &&
                (args.ChangedPaths is null || scope.QueryFirst(
                    "SELECT json_extract(payload_json,'$.changedPaths') FROM board_events WHERE machine_id='local' AND task_key=$task ORDER BY seq DESC LIMIT 1;",
                    r => r.IsNull(0) ? null : r.Text(0), new SqlArg("$task", args.TaskKey)) ==
                    JsonSerializer.Serialize(args.ChangedPaths, WorkflowSchema.Default.StringArray)))
                return new("local", args.TaskKey, task.Revision, args.Question?.QuestionKey,
                    args.Question is null ? null : args.Question.ExpectedGeneration + 1, null);
            next = next with { UpdatedAt = now, Revision = next.Revision + 1 };
            Commit(scope, next, question, kind, args.ChangedPaths, now);
            var accepted = question?.AnswerKey is null ? null : Acceptance(question);
            return new("local", next.TaskKey, next.Revision, question?.QuestionKey ?? args.Question?.QuestionKey, question?.Generation ?? (args.Question is null ? null : args.Question.ExpectedGeneration + 1), accepted);
        });
    }

    public (BoardAnswerResult Acceptance, bool Created) Answer(BoardAnswerArgs args, bool inChat = false)
    {
        ValidateAnswer(args);
        return db.Writer.Write("board.answer.intent", scope =>
        {
            if (Previous(scope, args) is { } previous) return (previous, false);
            var task = ReadTask(scope, "local", args.TaskKey) ?? throw new CallFailed("not_found");
            var now = Now;
            var question = Accept(scope, task, args, inChat, now);
            Commit(scope, task with { UpdatedAt = now, Revision = task.Revision + 1 }, question, "answer_recorded", null, now);
            return (Acceptance(question), true);
        });
    }

    private static void ValidateAnswer(BoardAnswerArgs args)
    {
        Text(args.TaskKey, 80, true); Text(args.QuestionKey, 80, true);
        if (args.MachineId != "local" || args.Generation < 1 || !Guid.TryParse(args.AnswerKey, out _) ||
            args.Answer is null || args.Answer.Kind is not ("text" or "approve" or "deny")) throw new CallFailed("invalid_argument");
        if (args.Answer.Kind == "text") Text(args.Answer.Text, 600, true);
        else if (args.Answer.Text is not null) throw new CallFailed("invalid_argument");
    }

    private static BoardQuestion Accept(IWriteScope scope, BoardTask task, BoardAnswerArgs args, bool inChat, long now)
    {
        var q = ReadQuestion(scope, "local", args.TaskKey, args.QuestionKey) ?? throw new CallFailed("not_found");
        if (q.Generation != args.Generation) throw new CallFailed("stale_generation");
        if (q.Answer is not null) throw new CallFailed("already_answered");
        if ((q.Kind == "text") != (args.Answer.Kind == "text")) throw new CallFailed("invalid_argument");
        return q with
        {
            Answer = args.Answer,
            AnsweredAt = now,
            AnswerKey = args.AnswerKey,
            DeliveryClientId = inChat ? null : "board-answer/" + args.AnswerKey,
            DeliveryState = inChat ? "recorded_in_chat" : "pending",
            AcceptedRevision = task.Revision + 1
        };
    }

    private static BoardAnswerResult? Previous(IWriteScope scope, BoardAnswerArgs args)
    {
        var q = scope.QueryFirst(
            "SELECT json_extract(payload_json,'$.questions[0]') FROM board_events WHERE machine_id='local' AND kind='answer_recorded' AND json_extract(payload_json,'$.questions[0].answerKey')=$key;",
            r => JsonSerializer.Deserialize(r.Text(0), WorkflowSchema.Default.BoardQuestion)!, new SqlArg("$key", args.AnswerKey));
        if (q is null) return null;
        if (q.TaskKey != args.TaskKey || q.QuestionKey != args.QuestionKey || q.Generation != args.Generation || q.Answer != args.Answer)
            throw new CallFailed("answer_key_conflict");
        return Acceptance(q);
    }

    private static BoardAnswerResult Acceptance(BoardQuestion q) =>
        new(q.MachineId, q.TaskKey, q.QuestionKey, q.Generation, q.AnswerKey!, q.AcceptedRevision!.Value);

    public void Settle(BoardAnswerArgs args, string state, string? turn) => db.Writer.Write("board.answer.settle", scope =>
    {
        var q = ReadQuestion(scope, "local", args.TaskKey, args.QuestionKey);
        if (q is null || q.Generation != args.Generation || q.AnswerKey != args.AnswerKey || q.DeliveryState != "pending") return 0;
        var task = ReadTask(scope, "local", args.TaskKey)!;
        var now = Now;
        var kind = state switch
        {
            "delivered" => "answer_delivered",
            "delivery_failed" => "answer_delivery_failed",
            "delivery_unknown" => "answer_delivery_unknown",
            _ => throw new CallFailed("invalid_argument")
        };
        Commit(scope, task with { UpdatedAt = now, Revision = task.Revision + 1 }, q with { DeliveryState = state, DeliveryTurnId = turn }, kind, null, now);
        return 1;
    });

    public void Recover()
    {
        // Selection is SQL; each pending delivery becomes its own complete source event.
        while (true)
        {
            var pending = db.Query("SELECT " + WorkflowQueries.QuestionRow + " FROM board_questions q WHERE machine_id='local' AND delivery_state='pending' LIMIT 100;",
                r => JsonSerializer.Deserialize(r.Text(0), WorkflowSchema.Default.BoardQuestion)!, CancellationToken.None);
            if (pending.Count == 0) return;
            foreach (var q in pending)
                Settle(new("local", q.TaskKey, q.QuestionKey, q.Generation, q.AnswerKey!, q.Answer!), "delivery_unknown", null);
        }
    }

    public void Apply(BoardEvent item, string machine) => db.Writer.Write("board.replay", scope =>
    {
        if (machine == "local") throw new CallFailed("invalid_argument");
        if (scope.QueryFirst("SELECT COUNT(*) FROM board_events WHERE machine_id=$machine AND seq=$seq;", r => r.Long(0),
            new SqlArg("$machine", machine), new SqlArg("$seq", item.Seq)) != 0) return 0;
        var task = item.Task with { MachineId = machine };
        var questions = Array.ConvertAll(item.Questions, q => q with { MachineId = machine });
        var normalized = item with { MachineId = machine, Task = task, Questions = questions };
        PutTask(scope, task);
        foreach (var q in questions) PutQuestion(scope, q);
        PutEvent(scope, normalized);
        return 1;
    });

    private static BoardTask? ReadTask(IWriteScope scope, string machine, string key) => scope.QueryFirst(
        "SELECT " + WorkflowQueries.TaskRow + " FROM board_tasks t WHERE machine_id=$machine AND task_key=$task;",
        r => JsonSerializer.Deserialize(r.Text(0), WorkflowSchema.Default.BoardTask)!, new SqlArg("$machine", machine), new SqlArg("$task", key));

    private static BoardQuestion? ReadQuestion(IWriteScope scope, string machine, string task, string question) => scope.QueryFirst(
        "SELECT " + WorkflowQueries.QuestionRow + " FROM board_questions q WHERE machine_id=$machine AND task_key=$task AND question_key=$question;",
        r => JsonSerializer.Deserialize(r.Text(0), WorkflowSchema.Default.BoardQuestion)!, new SqlArg("$machine", machine), new SqlArg("$task", task), new SqlArg("$question", question));

    private static void Commit(IWriteScope scope, BoardTask task, BoardQuestion? question, string kind, string[]? paths, long now)
    {
        var seq = scope.QueryFirst("SELECT COALESCE(MAX(seq),0)+1 FROM board_events WHERE machine_id='local';", r => r.Long(0));
        var item = new BoardEvent("local", seq, task.TaskKey, kind, now, task, question is null ? [] : [question], paths);
        var json = JsonSerializer.Serialize(item, WorkflowSchema.Default.BoardEvent);
        if (Encoding.UTF8.GetByteCount(json) > 24 * 1024) throw new CallFailed("invalid_argument");
        PutTask(scope, task);
        if (question is not null) PutQuestion(scope, question);
        PutEvent(scope, item);
    }

    private static void PutTask(IWriteScope scope, BoardTask t) => scope.Execute("""
        INSERT INTO board_tasks VALUES($m,$k,$c,$p,$t,$s,$state,$url,$pr,$start,$end,$at,$r)
        ON CONFLICT(machine_id,task_key) DO UPDATE SET title=excluded.title,summary=excluded.summary,state=excluded.state,
        task_url=excluded.task_url,pr_url=excluded.pr_url,ended_at=excluded.ended_at,updated_at=excluded.updated_at,revision=excluded.revision;
        """, new SqlArg("$m", t.MachineId), new SqlArg("$k", t.TaskKey), new SqlArg("$c", t.CreatorSessionId), new SqlArg("$p", t.Project), new SqlArg("$t", t.Title),
        new SqlArg("$s", t.Summary), new SqlArg("$state", t.State), new SqlArg("$url", t.TaskUrl), new SqlArg("$pr", t.PrUrl), new SqlArg("$start", t.StartedAt),
        new SqlArg("$end", t.EndedAt), new SqlArg("$at", t.UpdatedAt), new SqlArg("$r", t.Revision));

    private static void PutQuestion(IWriteScope scope, BoardQuestion q) => scope.Execute("""
        INSERT INTO board_questions VALUES($m,$t,$k,$g,$kind,$p,$rec,$at,$a,$answered,$key,$client,$turn,$state,$r)
        ON CONFLICT(machine_id,task_key,question_key) DO UPDATE SET generation=excluded.generation,kind=excluded.kind,
        prompt=excluded.prompt,recommendation=excluded.recommendation,asked_at=excluded.asked_at,answer_json=excluded.answer_json,
        answered_at=excluded.answered_at,answer_key=excluded.answer_key,delivery_client_id=excluded.delivery_client_id,
        delivery_turn_id=excluded.delivery_turn_id,delivery_state=excluded.delivery_state,accepted_revision=excluded.accepted_revision;
        """, new SqlArg("$m", q.MachineId), new SqlArg("$t", q.TaskKey), new SqlArg("$k", q.QuestionKey), new SqlArg("$g", q.Generation), new SqlArg("$kind", q.Kind),
        new SqlArg("$p", q.Prompt), new SqlArg("$rec", q.Recommendation), new SqlArg("$at", q.AskedAt),
        new SqlArg("$a", q.Answer is null ? null : JsonSerializer.Serialize(q.Answer, WorkflowSchema.Default.BoardAnswerValue)),
        new SqlArg("$answered", q.AnsweredAt), new SqlArg("$key", q.AnswerKey), new SqlArg("$client", q.DeliveryClientId), new SqlArg("$turn", q.DeliveryTurnId),
        new SqlArg("$state", q.DeliveryState), new SqlArg("$r", q.AcceptedRevision));

    private static void PutEvent(IWriteScope scope, BoardEvent item)
    {
        scope.Execute("""
            INSERT INTO board_events VALUES($m,$seq,$t,$kind,$at,json_remove($json,'$.machineId','$.seq','$.taskKey','$.kind','$.occurredAt'));
            """, new SqlArg("$m", item.MachineId), new SqlArg("$seq", item.Seq), new SqlArg("$t", item.TaskKey), new SqlArg("$kind", item.Kind), new SqlArg("$at", item.OccurredAt),
            new SqlArg("$json", JsonSerializer.Serialize(item, WorkflowSchema.Default.BoardEvent)));
        var key = JsonSerializer.Serialize(new[] { item.MachineId, item.TaskKey }, WorkflowSchema.Default.StringArray);
        scope.Publish("board", key, new BoardChange(item.MachineId, item.TaskKey, item.Task.Revision, item.Seq), WorkflowSchema.Default.BoardChange);
    }
}
