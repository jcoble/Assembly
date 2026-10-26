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

        await AnswerAsync(context, session => machines.For(session).SetConfigAsync(session, configId, value, context.RequestAborted));
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
        catch (MachineUnreachable error)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status502BadGateway, error.Message);
            return;
        }

        await JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
        {
            json.WriteStartObject();
            json.WritePropertyName("options");
            if (answer.Options is { } options)
                json.WriteRawValue(options);
            else
                json.WriteNullValue();
            json.WriteStartObject("choices");
            foreach (var (id, value) in answer.Choices)
                json.WriteString(id, value);
            json.WriteEndObject();
            json.WritePropertyName("commands");
            if (answer.Commands is { } commands)
                json.WriteRawValue(commands);
            else
                json.WriteNullValue();
            json.WriteEndObject();
        });
    }
}
