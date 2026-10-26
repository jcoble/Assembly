using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.Threads;
using Assembly.Domain.Threads;

namespace Assembly.Application.Features.Chat.Helpers;

/// <summary><c>list_sessions</c>: the sessions the caller can message, and how each stands.</summary>
public sealed class ListSessionsTool(SessionRegistry sessions, Reachability reach) : HelperTool
{
    public override string Name => "list_sessions";

    public override string Description =>
        "The sessions you can message: your parent, your helpers, their siblings, and other open sessions on this machine.";

    public override (string Name, string Description, bool Required)[] Parameters => [];

    public override Task<ToolResult> CallAsync(ThreadInfo caller, JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromResult(ToolResult.Json(json =>
        {
            json.WriteStartArray("sessions");
            foreach (var (thread, relation) in reach.Of(caller))
            {
                json.WriteStartObject();
                json.WriteString("threadId", thread.ThreadId);
                json.WriteString("title", thread.Title);
                json.WriteString("agent", thread.Agent);
                json.WriteString("machine", thread.Machine ?? "here");
                json.WriteString("relation", relation);
                json.WriteString("state", sessions.Working(thread.ThreadId) ? "working" : sessions.IsOpen(thread.ThreadId) ? "open" : "idle");
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }));
}
