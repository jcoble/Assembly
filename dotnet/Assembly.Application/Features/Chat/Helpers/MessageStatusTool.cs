using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.Threads;
using Assembly.Domain.Threads;

namespace Assembly.Application.Features.Chat.Helpers;

/// <summary><c>message_status</c>: how a message the caller sent stands, waiting up to 30 seconds for an answer.</summary>
public sealed class MessageStatusTool(ThreadStore threads, SessionRegistry sessions, LiveHub hub) : HelperTool
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static readonly ToolResult Working = ToolResult.Json(json => json.WriteString("state", "working"));

    public override string Name => "message_status";

    public override string Description =>
        "How a message you sent stands: working, answered (with the reply), or failed (with the error). " +
        "While it's working this waits up to 30 seconds for an answer before replying, so call it again rather than sleeping between calls.";

    public override (string Name, string Description, bool Required)[] Parameters =>
        [("messageId", "The messageId start_session or send_message returned.", true)];

    public override async Task<ToolResult> CallAsync(ThreadInfo caller, JsonElement arguments, CancellationToken cancellationToken)
    {
        var messageId = Argument(arguments, "messageId");
        var deadline = DateTimeOffset.UtcNow + Wait;
        var status = Status(caller, messageId);
        while (status == Working && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250, cancellationToken);
            status = Status(caller, messageId);
        }
        return status;
    }

    private ToolResult Status(ThreadInfo caller, string? messageId)
    {
        if (messageId is null || threads.Message(messageId) is not { } message || message.From != caller.ThreadId)
            return ToolResult.Error("No message of yours has that id.");
        // Answered from the other machine it went to.
        if (message.State is { } state)
            return Write(new RunAnswer(state, message.Reply ?? "", message.Error));
        if (message.Error is { } failed)
            return Write(new RunAnswer("failed", "", failed));
        if (message.RunId is not { } runId || sessions.ActiveRun(message.To) == runId)
            return Working;
        return RunAnswer.Of(hub, message.To, runId) is { } answer ? Write(answer) : Working;
    }

    private static ToolResult Write(RunAnswer answer) => ToolResult.Json(json =>
    {
        json.WriteString("state", answer.State);
        json.WriteString("reply", answer.Reply);
        if (answer.Error is not null)
            json.WriteString("error", answer.Error);
    });
}
