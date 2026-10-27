using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Rows;

using Assembly.Infrastructure.Mcp;
namespace Assembly.Infrastructure.Http;

/// <summary>
/// <c>GET /version</c>: the row format, which a Mac checks before it copies a remote machine's rows, and the commit
/// this backend was built from (scripts/check4.sh checks it).
/// <c>GET /machines</c>: the remote machines a new session can run on, besides this one.
/// <c>GET /agents?machine=</c>: the agents a new session on that machine (this one when left out) can run.
/// </summary>
internal sealed class InfoRoutes(MachineSet machines)
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/version", VersionAsync);
        routes.MapGet("/machines", MachinesAsync);
        routes.MapGet("/agents", AgentsAsync);
    }

    private async Task AgentsAsync(HttpContext context)
    {
        if (machines.Named(context.Request.Query["machine"].FirstOrDefault() is { Length: > 0 } name ? name : null) is not { } machine)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "unknown machine");
            return;
        }

        IReadOnlyList<(string Id, string Name)> agents;
        try
        {
            agents = await machine.AgentsAsync(context.RequestAborted);
        }
        catch (MachineUnreachable error)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status502BadGateway, error.Message);
            return;
        }

        await JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
        {
            json.WriteStartObject();
            json.WriteStartArray("agents");
            foreach (var (id, agentName) in agents)
            {
                json.WriteStartObject();
                json.WriteString("id", id);
                json.WriteString("name", agentName);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        });
    }

    private static Task VersionAsync(HttpContext context) =>
        JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
        {
            json.WriteStartObject();
            json.WriteNumber("formatVersion", RowStore.FormatVersion);
            json.WriteString("commit", Commit);
            json.WriteEndObject();
        });

    // The SDK stamps the git commit into the informational version: 1.0.0+<sha>.
    private static readonly string? Commit = typeof(InfoRoutes).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+') is [_, var sha] ? sha : null;

    private Task MachinesAsync(HttpContext context) =>
        JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
        {
            json.WriteStartObject();
            json.WriteStartArray("machines");
            foreach (var machine in machines.Remotes)
                json.WriteStringValue(machine.Name);
            json.WriteEndArray();
            json.WriteEndObject();
        });
}
