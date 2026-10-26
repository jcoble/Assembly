using System.Text.Json;
using Assembly.Application.Common.Machines;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.Threads;
using Assembly.Domain.Threads;

namespace Assembly.Application.Features.Chat.Helpers;

/// <summary><c>send_message</c>: a prompt to a session the caller can reach, steered into its turn if one is running.</summary>
public sealed class SendMessageTool(ThreadStore threads, SessionRegistry sessions, HelperDelivery delivery, Reachability reach, HelperRow row, HelperLimits limits) : HelperTool
{
    public override string Name => "send_message";

    public override string Description =>
        "Sends a message to another session you can reach (see list_sessions): it arrives as a prompt there. " +
        "Returns a messageId; read the answer with message_status.";

    public override (string Name, string Description, bool Required)[] Parameters =>
        [("threadId", "The session's threadId.", true), ("text", "The message.", true)];

    public override async Task<ToolResult> CallAsync(ThreadInfo caller, JsonElement arguments, CancellationToken cancellationToken)
    {
        var threadId = Argument(arguments, "threadId");
        var text = Argument(arguments, "text");
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(text))
            return ToolResult.Error("send_message needs a threadId and text.");
        var target = reach.Of(caller).Find(entry => entry.Thread.ThreadId == threadId).Thread;
        if (target is null)
            return ToolResult.Error($"You can't reach {threadId}. list_sessions shows the sessions you can.");
        if (threads.MessageCount(caller.ThreadId, threadId) >= limits.MessagesPerPair)
            return ToolResult.Error($"You've sent {limits.MessagesPerPair} messages to that session; that's the limit.");

        var message = threads.AddMessage(caller.ThreadId, threadId, text);
        // A parent on the machine that started this helper picks the message up from here.
        if (threads.Get(threadId) is null)
            return ToolResult.Json(json => json.WriteString("messageId", message.MessageId));

        var framed = MessageFrame.Of(message.MessageId, caller, text);
        string? runId;
        try
        {
            runId = await delivery.SendAsync(target, framed, cancellationToken);
        }
        catch (MachineUnreachable error)
        {
            threads.SetMessageFailed(message.MessageId, error.Message);
            return ToolResult.Error(error.Message);
        }
        if (runId is null)
        {
            threads.SetMessageFailed(message.MessageId, "That session was busy and couldn't take it.");
            return ToolResult.Error("That session is busy and can't take a message now; try again when it's done.");
        }
        threads.SetMessageRun(message.MessageId, runId);
        if (target is { Helper: true, ParentThreadId: { } parent })
        {
            sessions.TrackHelper(target.ThreadId, parent, runId);
            row.Show(target, "running");
        }
        return ToolResult.Json(json => json.WriteString("messageId", message.MessageId));
    }
}
