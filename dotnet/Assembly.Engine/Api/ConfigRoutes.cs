using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Assembly.Engine.Machines;
using Assembly.Engine.Threads;

namespace Assembly.Engine.Api;

/// <summary>
/// A thread's session config, on whichever machine runs its agent: <c>GET /threads/:id/config</c> answers
/// <c>{ options: [the agent's configOptions] | null, choices: { id: value }, commands: [its availableCommands] | null }</c>, and <c>POST /threads/:id/config
/// { configId, value }</c> chooses a value, answering the same.
/// </summary>
internal sealed class ConfigRoutes(ThreadStore threads, MachineSet machines)
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/threads/{id}/config", GetAsync);
        routes.MapPost("/threads/{id}/config", SetAsync);
    }

    private Task GetAsync(HttpContext context) =>
        AnswerAsync(context, thread => machines.For(thread).ConfigAsync(thread, context.RequestAborted));

    private async Task SetAsync(HttpContext context)
    {
        if (await JsonReply.ReadObjectAsync(context) is not { } body
            || JsonReply.String(body, "configId") is not { Length: > 0 } configId || JsonReply.String(body, "value") is not { } value)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "expected { configId, value }");
            return;
        }

        await AnswerAsync(context, thread => machines.For(thread).SetConfigAsync(thread, configId, value, context.RequestAborted));
    }

    private async Task AnswerAsync(HttpContext context, Func<ThreadInfo, Task<ThreadConfig>> config)
    {
        if (threads.Get(RequestValues.Route(context, "id")) is not { } thread)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status404NotFound, "unknown thread");
            return;
        }

        ThreadConfig answer;
        try
        {
            answer = await config(thread);
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
