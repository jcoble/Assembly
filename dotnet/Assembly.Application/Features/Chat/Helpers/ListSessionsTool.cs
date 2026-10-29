using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Helpers;

/// <summary><c>list_sessions</c>: the sessions the caller can message, and how each stands.</summary>
public sealed class ListSessionsTool(SessionRegistry sessions, Reachability reach) : HelperTool
{
    public override string Name => "list_sessions";

    public override string Description =>
        "The sessions you can message: the session that started you, sessions you started, their siblings, and other open sessions on this machine.";

    public override (string Name, string Description, bool Required)[] Parameters => [];

    public override Task<ToolResult> CallAsync(SessionInfo caller, JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromResult(ToolResult.Json(json =>
        {
            json.WriteStartArray("sessions");
            foreach (var (session, relation) in reach.Of(caller))
            {
                json.WriteStartObject();
                json.WriteString("sessionId", session.SessionId);
                json.WriteString("title", session.Title);
                json.WriteString("agent", session.Agent);
                json.WriteString("machine", session.Machine ?? "here");
                json.WriteString("relation", relation);
                json.WriteString("state", sessions.Working(session.SessionId) ? "working" : sessions.IsOpen(session.SessionId) ? "open" : "idle");
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }));
}
