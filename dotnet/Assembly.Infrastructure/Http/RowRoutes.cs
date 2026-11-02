using Microsoft.AspNetCore.Builder;
using System.Text.Json;
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
internal sealed class RowRoutes(LiveHub hub, SessionStore sessionStore, MachineSet machines,
    Assembly.Application.Common.Engine.IEngineDoor? ownerDoor = null)
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/sessions/{id}/rows", PageAsync);
        routes.MapGet("/sessions/{id}/tools/{item}", ToolAsync);
        if (ownerDoor is not null)
        {
            routes.MapGet("/sessions/{id}/owner-prompts", OwnerPromptsAsync);
            routes.MapGet("/sessions/{id}/owner-prompts/{client}/content", OwnerPromptContentAsync);
        }
    }

    private Task OwnerPromptsAsync(HttpContext context) => OwnerReply(context, () => ownerDoor!.ReadOwnerPromptsAsync(
        new(RequestValues.Route(context, "id"), context.Request.Query["machine"].FirstOrDefault(),
            OwnerNumber(context, "afterCreatedMs", null), context.Request.Query["afterClientId"].FirstOrDefault(),
            checked((int)OwnerNumber(context, "limit", 50)!.Value)), context.RequestAborted), SessionsJson.Default.OwnerPromptPage);

    private Task OwnerPromptContentAsync(HttpContext context) => OwnerReply(context, () => ownerDoor!.ReadOwnerPromptChunkAsync(
        new(RequestValues.Route(context, "id"), RequestValues.Route(context, "client"), context.Request.Query["contentSha256"].ToString(),
            context.Request.Query["part"].ToString(), OwnerNumber(context, "offset", 0)!.Value,
            checked((int)OwnerNumber(context, "count", 1024 * 1024)!.Value), context.Request.Query["machine"].FirstOrDefault()),
        context.RequestAborted), SessionsJson.Default.OwnerPromptChunk);

    private static long? OwnerNumber(HttpContext context, string key, long? fallback)
    {
        if (!context.Request.Query.ContainsKey(key)) return fallback;
        return RequestValues.QueryLong(context, key) ?? throw new ArgumentException("bad_args");
    }

    private static async Task OwnerReply<T>(HttpContext context, Func<Task<T>> read, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try
        {
            var result = await read();
            context.RequestAborted.ThrowIfCancellationRequested();
            await JsonReply.WriteAsync(context, StatusCodes.Status200OK, json => JsonSerializer.Serialize(json, result, type));
        }
        catch (KeyNotFoundException) { await JsonReply.ErrorAsync(context, 404, "unknown owner prompt"); }
        catch (InvalidOperationException error) { await JsonReply.ErrorAsync(context, 409, error.Message); }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        { await JsonReply.ErrorAsync(context, 400, "bad_args"); }
        catch (Exception error) when (error is MachineUnreachable or Assembly.Application.Common.Calls.CallFailed)
        { await JsonReply.ErrorAsync(context, 502, error.Message); }
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
            if (call.OutputBytes is { } bytes) json.WriteNumber("outputBytes", bytes);
            json.WriteBoolean("outputTruncated", call.OutputTruncated);
            if (call.Terminals is { } terminals)
            {
                json.WritePropertyName("terminals");
                JsonSerializer.Serialize(json, terminals, RowJsonContext.Rows.IReadOnlyListToolTerminalState);
            }
            json.WriteEndObject();
        });
    }
}
