using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Helpers;

/// <summary>
/// <c>start_session</c>: starts a helper session of any agent, here or on a machine this one reaches, under the
/// caller's session, on its prompt.
/// </summary>
public sealed class StartSessionTool(SessionStore sessionStore, SessionRegistry sessions, HelperDelivery delivery, HelperRow row, HelperLimits limits)
    : HelperTool
{
    public override string Name => "start_session";

    public override string Description =>
        "Starts a session: another agent session that works on the prompt you give it, in your working folder " +
        "(on another machine, in its own copy of your repo). Returns its sessionId and the messageId of the prompt; " +
        "read its answer with message_status. A session started by another session can't start sessions of its own.";

    public override (string Name, string Description, bool Required)[] Parameters =>
        [("prompt", "What the session should do. It sees only this, so say everything it needs.", true),
         ("agent", $"Which agent runs it: here, one of {string.Join(", ", sessions.Agents.Select(agent => agent.Id))}. Defaults to yours.", false),
         ("machine", $"Where it runs: one of {string.Join(", ", delivery.Names)}. Defaults to here.", false)];

    public override async Task<ToolResult> CallAsync(SessionInfo caller, JsonElement arguments, CancellationToken cancellationToken)
    {
        var prompt = Argument(arguments, "prompt");
        var agent = Argument(arguments, "agent") ?? caller.Agent;
        var machineName = Argument(arguments, "machine");
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Error("start_session needs a prompt.");
        if (caller.ParentSessionId is not null)
            return ToolResult.Error("A session started by another session can't start sessions of its own.");
        if (delivery.Named(machineName) is not { } machine)
            return ToolResult.Error($"There is no machine {machineName}. Machines: {string.Join(", ", delivery.Names)}.");
        // Another machine checks the agent itself.
        if (machine.Name is null && sessions.Agents.All(known => known.Id != agent))
            return ToolResult.Error($"There is no agent {agent} here. Agents: {string.Join(", ", sessions.Agents.Select(known => known.Id))}.");
        if (sessionStore.Helpers(caller.SessionId).Count(helper => sessions.Working(helper.SessionId)) >= limits.UnfinishedHelpers)
            return ToolResult.Error($"You already have {limits.UnfinishedHelpers} sessions at work; wait for one to finish.");

        SessionInfo helper;
        try
        {
            helper = await machine.CreateHelperAsync(caller, agent, SessionOpener.Title(prompt) ?? "Session", cancellationToken);
        }
        catch (Exception error) when (error is MachineUnreachable or CallFailed)
        {
            return ToolResult.Error(error.Message);
        }
        var message = sessionStore.AddMessage(caller.SessionId, helper.SessionId, prompt);
        var text = $"Another agent's session started you (session {caller.SessionId}, \"{caller.Title}\"). " +
                   "Do the task below; your answer goes back to it. You can message it with the Assembly MCP server's send_message tool.\n\n" + prompt;
        string? runId;
        try
        {
            runId = await machine.SendAsync(helper, text, null, cancellationToken);
        }
        catch (Exception error) when (error is MachineUnreachable or CallFailed)
        {
            sessionStore.SetMessageFailed(message.MessageId, error.Message);
            return ToolResult.Error(error.Message);
        }
        if (runId is null)
        {
            sessionStore.SetMessageFailed(message.MessageId, "The session couldn't take the prompt.");
            return ToolResult.Error("The session couldn't take the prompt; try again.");
        }
        sessionStore.SetMessageRun(message.MessageId, runId);
        sessions.TrackHelper(helper.SessionId, caller.SessionId, runId);
        row.Show(helper, "running");
        return ToolResult.Json(json =>
        {
            json.WriteString("sessionId", helper.SessionId);
            json.WriteString("messageId", message.MessageId);
        });
    }
}
