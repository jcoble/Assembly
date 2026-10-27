using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

using Assembly.Infrastructure.Mcp;
namespace Assembly.Infrastructure.Http;

/// <summary>A page of rows (<c>GET /sessions/:id/rows?before&amp;limit</c>) and an opened tool row's detail.</summary>
internal sealed class RowRoutes(LiveHub hub, SessionStore sessionStore, MachineSet machines)
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/sessions/{id}/rows", PageAsync);
        routes.MapGet("/sessions/{id}/tools/{item}", ToolAsync);
    }

    private Task PageAsync(HttpContext context)
    {
        var sessionId = RequestValues.Route(context, "id");
        var limit = (int)Math.Clamp(RequestValues.QueryLong(context, "limit") ?? 50, 1, 500);
        var page = hub.Page(sessionId, RequestValues.QueryLong(context, "before") ?? long.MaxValue, limit, context.RequestAborted);
        var session = sessionStore.Get(sessionId);
        var machine = session is null ? null : machines.Named(session.Machine);
        machine?.Watch(session!);
        var activeRun = machine?.ActiveRun(session!);
        return JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
        {
            json.WriteStartObject();
            json.WriteStartArray("rows");
            foreach (var row in page.Rows)
                RowWire.Write(json, row);
            json.WriteEndArray();
            json.WriteBoolean("hasBefore", page.HasBefore);
            if (page.Before is { } before)
                json.WriteNumber("before", before);
            else
                json.WriteNull("before");
            json.WriteString("activeRunId", activeRun);
            json.WriteEndObject();
        });
    }

    private async Task ToolAsync(HttpContext context)
    {
        StoredToolCall? call = null;
        try
        {
            if (sessionStore.Get(RequestValues.Route(context, "id")) is { } session)
                call = await machines.For(session).ToolCallAsync(session, RequestValues.Route(context, "item"), context.RequestAborted);
        }
        catch (Exception error) when (error is MachineUnreachable or Assembly.Application.Common.Calls.CallFailed)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status502BadGateway, error.Message);
            return;
        }

        if (call is null)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status404NotFound, "unknown tool call");
            return;
        }

        await JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
        {
            json.WriteStartObject();
            json.WriteString("summary", call.Summary);
            json.WriteString("input", call.Input);
            json.WriteString("output", call.Output);
            json.WriteString("diff", call.Diff);
            json.WriteString("path", call.Path);
            json.WriteEndObject();
        });
    }
}
