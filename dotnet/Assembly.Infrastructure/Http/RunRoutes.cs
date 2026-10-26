using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

using Assembly.Infrastructure.Mcp;
namespace Assembly.Infrastructure.Http;

/// <summary>
/// <c>POST /threads/:id/send {text}</c> starts a turn, or steers the one running, <c>POST /runs/:id/stop</c> stops one,
/// <c>POST /threads/:id/asks/:ask {answer}</c> answers a card the agent opened, and
/// <c>POST /threads/:id/tasks/:task/stop</c> stops background work, on whichever machine runs the thread's agent. An
/// ask that is no longer open, an answer that doesn't fit it, or a task that can't be stopped is a 409.
/// </summary>
internal sealed class RunRoutes(LiveHub hub, SessionStore sessionStore, MachineSet machines)
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/sessions/{id}/send", SendAsync);
        routes.MapPost("/runs/{id}/stop", StopAsync);
        routes.MapPost("/sessions/{id}/asks/{ask}", AnswerAsync);
        routes.MapPost("/sessions/{id}/tasks/{task}/stop", StopTaskAsync);
    }

    private async Task StopTaskAsync(HttpContext context)
    {
        if (sessionStore.Get(RequestValues.Route(context, "id")) is not { } session)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status404NotFound, "unknown session");
            return;
        }

        try
        {
            // A helper on another machine stops there.
            var task = RequestValues.Route(context, "task");
            var stopped = sessionStore.Get(task) is { Helper: true, Machine: not null } helper && helper.ParentSessionId == session.SessionId
                ? machines.For(helper).ActiveRun(helper) is { } run && await machines.For(helper).StopAsync(helper, run, context.RequestAborted)
                : await machines.For(session).StopTaskAsync(session, task, context.RequestAborted);
            if (!stopped)
            {
                await JsonReply.ErrorAsync(context, StatusCodes.Status409Conflict, "that task isn't running, or can't be stopped");
                return;
            }
        }
        catch (MachineUnreachable error)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status502BadGateway, error.Message);
            return;
        }

        await JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
        {
            json.WriteStartObject();
            json.WriteBoolean("stopped", true);
            json.WriteEndObject();
        });
    }

    private async Task AnswerAsync(HttpContext context)
    {
        if (await JsonReply.ReadObjectAsync(context) is not { } answer)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "expected { optionId } or { action, content }");
            return;
        }

        if (sessionStore.Get(RequestValues.Route(context, "id")) is not { } session)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status404NotFound, "unknown session");
            return;
        }

        try
        {
            if (!await machines.For(session).AnswerAsync(session, RequestValues.Route(context, "ask"), answer, context.RequestAborted))
            {
                await JsonReply.ErrorAsync(context, StatusCodes.Status409Conflict, "that ask isn't open, or the answer doesn't fit it");
                return;
            }
        }
        catch (MachineUnreachable error)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status502BadGateway, error.Message);
            return;
        }

        await JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
        {
            json.WriteStartObject();
            json.WriteBoolean("answered", true);
            json.WriteEndObject();
        });
    }

    private async Task SendAsync(HttpContext context)
    {
        if (await JsonReply.ReadObjectAsync(context) is not { } body || JsonReply.String(body, "text") is not { } text)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "expected { text }");
            return;
        }

        if (sessionStore.Get(RequestValues.Route(context, "id")) is not { } session)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status404NotFound, "unknown session");
            return;
        }

        // A helper whose parent is on the machine that reaches this one takes its prompts from there.
        if (session.ParentSessionId is { } parent && sessionStore.Get(parent) is not null)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "a subagent's session takes prompts from its parent only");
            return;
        }

        string? runId;
        try
        {
            runId = await machines.For(session).SendAsync(session, text, context.RequestAborted);
        }
        catch (MachineUnreachable error)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status502BadGateway, error.Message);
            return;
        }

        if (runId is null)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status409Conflict, "the running turn can't take a message yet");
            return;
        }

        await JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
        {
            json.WriteStartObject();
            json.WriteString("runId", runId);
            json.WriteEndObject();
        });
    }

    private async Task StopAsync(HttpContext context)
    {
        var runId = RequestValues.Route(context, "id");
        try
        {
            var stopped = hub.SessionOfRun(runId) is { } sessionId && sessionStore.Get(sessionId) is { } session
                && await machines.For(session).StopAsync(session, runId, context.RequestAborted);
            context.Response.StatusCode = stopped ? StatusCodes.Status202Accepted : StatusCodes.Status404NotFound;
        }
        catch (MachineUnreachable error)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status502BadGateway, error.Message);
        }
    }
}
