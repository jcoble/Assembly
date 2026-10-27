using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

using Assembly.Infrastructure.Mcp;
namespace Assembly.Infrastructure.Http;

/// <summary>
/// Messages this machine's sessions sent to sessions on the machine that reaches it (a helper's parent on the desktop),
/// which can't be pushed there: <c>GET /outbox</c> lists those waiting, <c>POST /outbox/:id {runId}</c> or
/// <c>{error}</c> says how delivering went, and <c>POST /outbox/:id/answer {state, reply, error?}</c> brings the
/// answer back.
/// </summary>
internal sealed class OutboxRoutes(SessionStore sessionStore)
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/outbox", ListAsync);
        routes.MapPost("/outbox/{id}", DeliveredAsync);
        routes.MapPost("/outbox/{id}/answer", AnswerAsync);
    }

    private Task ListAsync(HttpContext context) => JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
    {
        json.WriteStartObject();
        json.WriteStartArray("messages");
        foreach (var message in sessionStore.Outbox())
        {
            json.WriteStartObject();
            json.WriteString("messageId", message.MessageId);
            json.WriteString("from", message.From);
            json.WriteString("to", message.To);
            json.WriteString("text", message.Text);
            json.WriteNumber("createdAt", message.CreatedAt);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    });

    private async Task DeliveredAsync(HttpContext context)
    {
        var id = RequestValues.Route(context, "id");
        if (await JsonReply.ReadObjectAsync(context) is not { } body || sessionStore.Message(id) is null)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status404NotFound, "unknown message");
            return;
        }
        if (JsonReply.String(body, "runId") is { } runId)
            sessionStore.SetMessageRun(id, runId);
        else
            sessionStore.SetMessageFailed(id, JsonReply.String(body, "error") ?? "It couldn't be delivered.");
        await Ok(context);
    }

    private async Task AnswerAsync(HttpContext context)
    {
        var id = RequestValues.Route(context, "id");
        if (await JsonReply.ReadObjectAsync(context) is not { } body || JsonReply.String(body, "state") is not { } state || sessionStore.Message(id) is null)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "expected a known message and { state, reply, error? }");
            return;
        }
        sessionStore.SetMessageAnswer(id, state, JsonReply.String(body, "reply") ?? "", JsonReply.String(body, "error"));
        await Ok(context);
    }

    private static Task Ok(HttpContext context) => JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
    {
        json.WriteStartObject();
        json.WriteBoolean("ok", true);
        json.WriteEndObject();
    });
}
