using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Helpers;

/// <summary><c>send_message</c>: a prompt to a session the caller can reach, steered into its turn if one is running.</summary>
public sealed class SendMessageTool(SessionStore sessionStore, SessionRegistry sessions, HelperDelivery delivery, Reachability reach, HelperRow row, HelperLimits limits) : HelperTool
{
    public override string Name => "send_message";

    public override string Description =>
        "Sends a message to another session you can reach (see list_sessions): it arrives as a prompt there. " +
        "Returns a messageId; read the answer with message_status.";

    public override (string Name, string Description, bool Required)[] Parameters =>
        [("sessionId", "The session's id.", true), ("text", "The message.", true)];

    public override async Task<ToolResult> CallAsync(SessionInfo caller, JsonElement arguments, CancellationToken cancellationToken)
    {
        var sessionId = Argument(arguments, "sessionId");
        var text = Argument(arguments, "text");
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(text))
            return ToolResult.Error("send_message needs a sessionId and text.");
        var target = reach.Of(caller).Find(entry => entry.Session.SessionId == sessionId).Session;
        if (target is null)
            return ToolResult.Error($"You can't reach {sessionId}. list_sessions shows the sessions you can.");
        if (sessionStore.MessageCount(caller.SessionId, sessionId) >= limits.MessagesPerPair)
            return ToolResult.Error($"You've sent {limits.MessagesPerPair} messages to that session; that's the limit.");

        var message = sessionStore.AddMessage(caller.SessionId, sessionId, text);
        // A parent on the machine that started this helper picks the message up from here.
        if (sessionStore.Get(sessionId) is null)
            return ToolResult.Json(json => json.WriteString("messageId", message.MessageId));

        var framed = MessageFrame.Of(message.MessageId, caller, text);
        string? runId;
        try
        {
            runId = await delivery.SendAsync(target, framed, cancellationToken);
        }
        catch (Exception error) when (error is MachineUnreachable or CallFailed)
        {
            sessionStore.SetMessageFailed(message.MessageId, error.Message);
            return ToolResult.Error(error.Message);
        }
        if (runId is null)
        {
            sessionStore.SetMessageFailed(message.MessageId, "That session was busy and couldn't take it.");
            return ToolResult.Error("That session is busy and can't take a message now; try again when it's done.");
        }
        sessionStore.SetMessageRun(message.MessageId, runId);
        if (target is { Helper: true, ParentSessionId: { } parent })
        {
            sessions.TrackHelper(target.SessionId, parent, runId);
            row.Show(target, "running");
        }
        return ToolResult.Json(json => json.WriteString("messageId", message.MessageId));
    }
}
