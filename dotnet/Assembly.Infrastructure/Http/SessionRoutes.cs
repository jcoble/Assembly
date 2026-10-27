using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

using Assembly.Infrastructure.Mcp;
namespace Assembly.Infrastructure.Http;

/// <summary>
/// <c>GET /sessions</c> (the sessions list), <c>GET /sessions/:id</c>, and <c>POST /sessions {agent, repoUrl, machine?}</c> (an agent from <c>GET /agents</c>),
/// which creates the session on the machine named, or on this one.
/// </summary>
internal sealed class SessionRoutes(SessionStore sessionStore, MachineSet machines)
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/sessions", ListAsync);
        routes.MapGet("/sessions/{id}", GetAsync);
        routes.MapPost("/sessions", CreateAsync);
    }

    private Task ListAsync(HttpContext context)
    {
        var limit = (int)Math.Clamp(RequestValues.QueryLong(context, "limit") ?? 50, 1, 200);
        var page = sessionStore.List(RequestValues.QueryLong(context, "before"), limit, context.RequestAborted);
        return JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
        {
            json.WriteStartObject();
            json.WriteStartArray("sessions");
            foreach (var session in page.Sessions)
                Write(json, session);
            json.WriteEndArray();
            if (page.NextBefore is { } next)
                json.WriteNumber("nextBefore", next);
            else
                json.WriteNull("nextBefore");
            json.WriteEndObject();
        });
    }

    private Task GetAsync(HttpContext context)
    {
        if (sessionStore.Get(RequestValues.Route(context, "id")) is not { } session)
            return JsonReply.ErrorAsync(context, StatusCodes.Status404NotFound, "unknown session");
        return JsonReply.WriteAsync(context, StatusCodes.Status200OK, json => Write(json, session));
    }

    private async Task CreateAsync(HttpContext context)
    {
        if (await JsonReply.ReadObjectAsync(context) is not { } body
            || JsonReply.String(body, "agent") is not { Length: > 0 } agent
            || JsonReply.String(body, "repoUrl") is not { Length: > 0 } repoUrl)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "expected { agent, repoUrl, machine?, parentSessionId?, title? }");
            return;
        }

        if (machines.Named(JsonReply.String(body, "machine")) is not { } machine)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "unknown machine");
            return;
        }

        SessionInfo session;
        try
        {
            if (!(await machine.AgentsAsync(context.RequestAborted)).Any(known => known.Id == agent))
            {
                await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, $"this machine has no agent {agent}");
                return;
            }
            // A helper session, started by a session on the machine that reaches this one.
            session = JsonReply.String(body, "parentSessionId") is { } parentId
                ? await machine.CreateHelperAsync(new SessionInfo(parentId, agent, repoUrl, null, null, 0), agent,
                    JsonReply.String(body, "title") ?? "Helper", context.RequestAborted)
                : await machine.CreateSessionAsync(agent, repoUrl, context.RequestAborted);
        }
        catch (Exception error) when (error is MachineUnreachable or Assembly.Application.Common.Calls.CallFailed)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status502BadGateway, error.Message);
            return;
        }

        await JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
        {
            json.WriteStartObject();
            json.WriteString("sessionId", session.SessionId);
            json.WriteEndObject();
        });
    }

    private static void Write(Utf8JsonWriter json, SessionInfo session)
    {
        json.WriteStartObject();
        json.WriteString("sessionId", session.SessionId);
        json.WriteString("agent", session.Agent);
        json.WriteString("repoUrl", session.RepoUrl);
        json.WriteString("title", session.Title);
        json.WriteString("machine", session.Machine);
        json.WriteString("parentSessionId", session.ParentSessionId);
        json.WriteString("acpSessionId", session.AcpSessionId);
        json.WriteNumber("updatedAt", session.UpdatedAt);
        json.WriteEndObject();
    }
}
