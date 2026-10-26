using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Application.Features.Chat.Threads;
using Assembly.Domain.Threads;

using Assembly.Infrastructure.Mcp;
namespace Assembly.Infrastructure.Http;

/// <summary>
/// <c>GET /threads</c> (the sessions list), <c>GET /threads/:id</c>, and <c>POST /threads {agent, repoUrl, machine?}</c> (an agent from <c>GET /agents</c>),
/// which creates the thread on the machine named, or on this one.
/// </summary>
internal sealed class ThreadRoutes(ThreadStore threads, MachineSet machines)
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/threads", ListAsync);
        routes.MapGet("/threads/{id}", GetAsync);
        routes.MapPost("/threads", CreateAsync);
    }

    private Task ListAsync(HttpContext context)
    {
        var limit = (int)Math.Clamp(RequestValues.QueryLong(context, "limit") ?? 50, 1, 200);
        var page = threads.List(RequestValues.QueryLong(context, "before"), limit);
        return JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
        {
            json.WriteStartObject();
            json.WriteStartArray("threads");
            foreach (var thread in page.Threads)
                Write(json, thread);
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
        if (threads.Get(RequestValues.Route(context, "id")) is not { } thread)
            return JsonReply.ErrorAsync(context, StatusCodes.Status404NotFound, "unknown thread");
        return JsonReply.WriteAsync(context, StatusCodes.Status200OK, json => Write(json, thread));
    }

    private async Task CreateAsync(HttpContext context)
    {
        if (await JsonReply.ReadObjectAsync(context) is not { } body
            || JsonReply.String(body, "agent") is not { Length: > 0 } agent
            || JsonReply.String(body, "repoUrl") is not { Length: > 0 } repoUrl)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "expected { agent, repoUrl, machine?, parentThreadId?, title? }");
            return;
        }

        if (machines.Named(JsonReply.String(body, "machine")) is not { } machine)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "unknown machine");
            return;
        }

        ThreadInfo thread;
        try
        {
            if (!(await machine.AgentsAsync(context.RequestAborted)).Any(known => known.Id == agent))
            {
                await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, $"this machine has no agent {agent}");
                return;
            }
            // A helper session's thread, started by a session on the machine that reaches this one.
            thread = JsonReply.String(body, "parentThreadId") is { } parentId
                ? await machine.CreateHelperAsync(new ThreadInfo(parentId, agent, repoUrl, null, null, 0), agent,
                    JsonReply.String(body, "title") ?? "Helper", context.RequestAborted)
                : await machine.CreateThreadAsync(agent, repoUrl, context.RequestAborted);
        }
        catch (MachineUnreachable error)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status502BadGateway, error.Message);
            return;
        }

        await JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
        {
            json.WriteStartObject();
            json.WriteString("threadId", thread.ThreadId);
            json.WriteEndObject();
        });
    }

    private static void Write(Utf8JsonWriter json, ThreadInfo thread)
    {
        json.WriteStartObject();
        json.WriteString("threadId", thread.ThreadId);
        json.WriteString("agent", thread.Agent);
        json.WriteString("repoUrl", thread.RepoUrl);
        json.WriteString("title", thread.Title);
        json.WriteString("machine", thread.Machine);
        json.WriteString("parentThreadId", thread.ParentThreadId);
        json.WriteString("acpSessionId", thread.AcpSessionId);
        json.WriteNumber("updatedAt", thread.UpdatedAt);
        json.WriteEndObject();
    }
}
