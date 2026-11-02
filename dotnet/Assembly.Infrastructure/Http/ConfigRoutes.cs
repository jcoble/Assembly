using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions.Configuration;

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
        AnswerAsync(context, (machine, session) => machine.ConfigAsync(session, context.RequestAborted));

    private async Task DiscoverAsync(HttpContext context)
    {
        if (await JsonReply.ReadObjectAsync(context) is not { } body
            || JsonReply.String(body, "agent") is not { Length: > 0 } agent
            || JsonReply.String(body, "targetId") is not { Length: > 0 } target
            || JsonReply.String(body, "cwd") is not { Length: > 0 } cwd)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "expected { agent, targetId, cwd }");
            return;
        }
        var machine = machines.Named(JsonReply.String(body, "machine"));
        if (machine is null)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "unknown machine");
            return;
        }
        try
        {
            var answer = await machine.DiscoverConfigAsync(agent, target, cwd, context.RequestAborted);
            Current(machine);
            await JsonReply.WriteAsync(context, StatusCodes.Status200OK, json => ComposerConfigJson.Write(json, answer.Config, answer.ModelOptions));
        }
        catch (Exception error) when (error is MachineUnreachable or CallFailed)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status502BadGateway, error.Message);
        }
    }

    private async Task WarmAsync(HttpContext context)
    {
        if (await JsonReply.ReadObjectAsync(context) is not { } body || JsonReply.String(body, "targetId") is not { Length: > 0 } target)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "expected { targetId }");
            return;
        }
        await AnswerAsync(context, (machine, session) => machine.WarmConfigAsync(session, target, context.RequestAborted));
    }

    private async Task SetAsync(HttpContext context)
    {
        if (await JsonReply.ReadObjectAsync(context) is not { } body
            || JsonReply.String(body, "configId") is not { Length: > 0 } configId || JsonReply.String(body, "value") is not { } value)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "expected { configId, value }");
            return;
        }

        await AnswerAsync(context, (machine, session) => machine.SetConfigAsync(session, configId, value, context.RequestAborted, JsonReply.String(body, "targetId")));
    }

    private async Task AnswerAsync(HttpContext context, Func<IMachine, SessionInfo, Task<SessionConfig>> config)
    {
        if (sessionStore.Get(RequestValues.Route(context, "id")) is not { } session)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status404NotFound, "unknown session");
            return;
        }

        SessionConfig answer;
        try
        {
            var machine = machines.For(session);
            answer = await config(machine, session);
            Current(machine);
        }
        catch (Exception error) when (error is MachineUnreachable or Assembly.Application.Common.Calls.CallFailed)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status502BadGateway, error.Message);
            return;
        }

        await JsonReply.WriteAsync(context, StatusCodes.Status200OK, json => ComposerConfigJson.Write(json, answer));
    }

    private void Current(IMachine machine)
    {
        if (!ReferenceEquals(machines.Named(machine.Name), machine)) throw new CallFailed("The selected machine changed.");
    }
}
