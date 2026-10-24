using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Assembly.Engine.Live;
using Assembly.Engine.Machines;
using Assembly.Engine.Store;
using Assembly.Engine.Threads;

namespace Assembly.Engine.Api;

/// <summary>A page of rows (<c>GET /threads/:id/rows?before&amp;limit</c>) and an opened tool row's detail.</summary>
internal sealed class RowRoutes(LiveHub hub, ThreadStore threads, MachineSet machines)
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/threads/{id}/rows", PageAsync);
        routes.MapGet("/threads/{id}/tools/{item}", ToolAsync);
    }

    private Task PageAsync(HttpContext context)
    {
        var threadId = RequestValues.Route(context, "id");
        var limit = (int)Math.Clamp(RequestValues.QueryLong(context, "limit") ?? 50, 1, 500);
        var page = hub.Page(threadId, RequestValues.QueryLong(context, "before") ?? long.MaxValue, limit);
        var thread = threads.Get(threadId);
        var machine = thread is null ? null : machines.Named(thread.Machine);
        machine?.Watch(thread!);
        var activeRun = machine?.ActiveRun(thread!);
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
            if (threads.Get(RequestValues.Route(context, "id")) is { } thread)
                call = await machines.For(thread).ToolCallAsync(thread, RequestValues.Route(context, "item"), context.RequestAborted);
        }
        catch (MachineUnreachable error)
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
