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
/// A session's config, on whichever machine runs its agent: <c>GET /sessions/:id/config</c> answers
/// <c>{ options: [the agent's configOptions] | null, choices: { id: value }, commands: [its availableCommands] | null }</c>, and <c>POST /sessions/:id/config
/// { configId, value }</c> chooses a value, answering the same.
/// </summary>
internal sealed class ConfigRoutes(SessionStore sessionStore, MachineSet machines)
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/sessions/{id}/config", GetAsync);
        routes.MapPost("/sessions/{id}/config", SetAsync);
        routes.MapPost("/config/discover", DiscoverAsync);
        routes.MapPost("/sessions/{id}/config/warm", WarmAsync);
    }

    private Task GetAsync(HttpContext context) =>
        AnswerAsync(context, session => machines.For(session).ConfigAsync(session, context.RequestAborted));

    private async Task SetAsync(HttpContext context)
    {
        if (await JsonReply.ReadObjectAsync(context) is not { } body
            || JsonReply.String(body, "configId") is not { Length: > 0 } configId || JsonReply.String(body, "value") is not { } value)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "expected { configId, value }");
            return;
        }

        await AnswerAsync(context, async session =>
        {
            var machine = machines.For(session);
            var result = await machine.SetConfigAsync(session, configId, value, context.RequestAborted, JsonReply.String(body, "targetId"));
            if (!ReferenceEquals(machines.For(session), machine)) throw new Assembly.Application.Common.Calls.CallFailed("The selected machine changed.");
            return result;
        });
    }

    private async Task AnswerAsync(HttpContext context, Func<SessionInfo, Task<SessionConfig>> config)
    {
        if (sessionStore.Get(RequestValues.Route(context, "id")) is not { } session)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status404NotFound, "unknown session");
            return;
        }

        SessionConfig answer;
        try
        {
            answer = await config(session);
        }
        catch (Exception error) when (error is MachineUnreachable or Assembly.Application.Common.Calls.CallFailed)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status502BadGateway, error.Message);
            return;
        }

        await JsonReply.WriteAsync(context, StatusCodes.Status200OK, json => ComposerConfigJson.Write(json, answer));
    }

    private async Task WarmAsync(HttpContext context)
    {
        if (await JsonReply.ReadObjectAsync(context) is not { } body || JsonReply.String(body, "targetId") is not { Length: > 0 } target)
        { await JsonReply.ErrorAsync(context, 400, "expected {targetId}"); return; }
        await AnswerAsync(context, async session =>
        {
            var machine = machines.For(session);
            var result = await machine.WarmConfigAsync(session, target, context.RequestAborted);
            if (!ReferenceEquals(machines.For(session), machine)) throw new Assembly.Application.Common.Calls.CallFailed("The selected machine changed.");
            return result;
        });
    }

    private async Task DiscoverAsync(HttpContext context)
    {
        if (await JsonReply.ReadObjectAsync(context) is not { } body || JsonReply.String(body, "agent") is not { Length: > 0 } agent
            || JsonReply.String(body, "targetId") is not { Length: > 0 } target || JsonReply.String(body, "cwd") is not { } cwd)
        { await JsonReply.ErrorAsync(context, 400, "expected {agent,targetId,cwd}"); return; }
        try
        {
            var machine = machines.Named(null)!;
            var result = await machine.DiscoverConfigAsync(agent, target, cwd, context.RequestAborted);
            await JsonReply.WriteAsync(context, 200, json => ComposerConfigJson.Write(json, result.Config, result.ModelOptions));
        }
        catch (Exception error) when (error is MachineUnreachable or Assembly.Application.Common.Calls.CallFailed)
        { await JsonReply.ErrorAsync(context, 502, error.Message); }
    }
}
