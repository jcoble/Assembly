using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Workflows;
using Assembly.Application.Features.Workflows.Contracts;
using Assembly.Infrastructure.Workflows;

namespace Assembly.Infrastructure.Mcp;

/// <summary>Token-owned reporting and host-owned answer delivery. No caller can select its creator or project.</summary>
public sealed class BoardTools(SessionStore sessions, SessionRegistry registry, WorkflowTransitions transitions, WorkflowQueries queries, CancellationToken hostStop) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(hostStop);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<BoardAnswerResult>> _deliveries = [];
    private bool _disposed;

    public BoardUpdateResult Update(string caller, BoardUpdateArgs args)
    {
        var session = sessions.Get(caller) ?? throw new CallFailed("not_found");
        if (session.Helper) throw new CallFailed("helper_forbidden");
        if (session.Machine is not null) throw new CallFailed("not_owner");
        return transitions.Update(session.SessionId, session.RepoUrl, args);
    }

    public async Task<BoardAnswerResult> AnswerAsync(BoardAnswerArgs args, CancellationToken cancel)
    {
        Task<BoardAnswerResult> delivery;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var (accepted, created) = transitions.Answer(args);
            if (!created) return accepted;
            delivery = Task.Run(async () =>
            {
                try { return await DeliverAsync(args, accepted); }
                finally { lock (_gate) _deliveries.Remove(args.AnswerKey); }
            }, CancellationToken.None);
            _deliveries.Add(args.AnswerKey, delivery);
        }
        return await delivery.WaitAsync(cancel);
    }

    private async Task<BoardAnswerResult> DeliverAsync(BoardAnswerArgs args, BoardAnswerResult accepted)
    {
        string? turn = null;
        var state = "delivery_unknown";
        try
        {
            var task = queries.Task(new("local", args.TaskKey, null, null), _stop.Token).Task;
            var session = sessions.Get(task.CreatorSessionId) ?? throw new CallFailed("not_found");
            var identity = $"question \"{args.QuestionKey}\" (task \"{args.TaskKey}\", generation {args.Generation}, answer key \"{args.AnswerKey}\")";
            var text = args.Answer.Kind switch
            {
                "approve" => "Approved: " + identity,
                "deny" => "Denied: " + identity,
                _ => $"Answer to {identity}: {args.Answer.Text}"
            };
            turn = await registry.For(session.SessionId).SendAfterTurnAsync(text, "board-answer/" + args.AnswerKey, _stop.Token);
            state = turn is null ? "delivery_failed" : "delivered";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Intent is durable. An uncertain send never triggers an automatic resend.
        }
        transitions.Settle(args, state, turn);
        return accepted;
    }

    public void WriteTool(Utf8JsonWriter json)
    {
        using var schema = JsonDocument.Parse(Schema);
        json.WriteStartObject();
        json.WriteString("name", "board_update");
        json.WriteString("description", "Report your task progress, ask an independent question, or record an answer already received in chat. Helpers cannot report.");
        json.WritePropertyName("inputSchema");
        schema.RootElement.WriteTo(json);
        json.WriteEndObject();
    }

    public (string Text, bool IsError) Call(string caller, JsonElement args)
    {
        try
        {
            if (args.ValueKind == JsonValueKind.Undefined) return ("bad_args", true);
            var input = args.Deserialize(WorkflowJson.Default.BoardUpdateArgs) ?? throw new CallFailed("invalid_argument");
            return (JsonSerializer.Serialize(Update(caller, input), WorkflowJson.Default.BoardUpdateResult), false);
        }
        catch (JsonException) { return ("bad_args", true); }
        catch (CallFailed error) { return (error.Message, true); }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate) { _disposed = true; pending = [.. _deliveries.Values]; }
        await _stop.CancelAsync();
        await Task.WhenAll(pending);
        _stop.Dispose();
    }

    private const string Schema = """
        {"type":"object","additionalProperties":false,
         "required":["taskKey","title","state","summary","taskUrl","prUrl","changedPaths","question","answerReceived"],
         "properties":{
          "taskKey":{"type":"string","maxLength":80},"title":{"type":"string","maxLength":120},
          "state":{"type":"string","enum":["queued","running","paused","waiting","completed","failed","cancelled"]},
          "summary":{"type":"string","maxLength":600},"taskUrl":{"type":["string","null"],"maxLength":2048},
          "prUrl":{"type":["string","null"],"maxLength":2048},
          "changedPaths":{"type":["array","null"],"maxItems":32,"items":{"type":"string","maxLength":240}},
          "question":{"type":["object","null"],"additionalProperties":false,
            "required":["questionKey","expectedGeneration","kind","prompt","recommendation"],
            "properties":{"questionKey":{"type":"string","maxLength":80},"expectedGeneration":{"type":"integer","minimum":0},
             "kind":{"type":"string","enum":["text","approval"]},"prompt":{"type":"string","maxLength":1200},
             "recommendation":{"type":["string","null"],"maxLength":300}}},
          "answerReceived":{"type":["object","null"],"additionalProperties":false,
            "required":["questionKey","generation","answerKey","answer"],"properties":{
             "questionKey":{"type":"string","maxLength":80},"generation":{"type":"integer","minimum":1},
             "answerKey":{"type":"string"},"answer":{"type":"object","additionalProperties":false,
              "required":["kind","text"],"properties":{"kind":{"type":"string","enum":["text","approve","deny"]},
               "text":{"type":["string","null"],"maxLength":600}}}}}}}
        """;
}
