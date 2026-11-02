using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Mcp.Tools;

public sealed record GetSessionTitleArguments(string Session, long? Version = null, long? Epoch = null,
    long? TotalBytes = null, long? Offset = null, int Count = 1024 * 1024);

public sealed class GetSessionTitleTool(McpGate gate)
    : EngineTool("get_session_title", "Read the exact title of this session or a related session.", """
        {"type":"object","additionalProperties":false,"properties":{"session":{"type":"string"},"version":{"type":"integer"},"epoch":{"type":"integer"},"totalBytes":{"type":"integer"},"offset":{"type":"integer"},"count":{"type":"integer"}},"required":["session"]}
        """)
{
    protected override ValueTask<JsonElement> CallAsync(McpCaller caller, JsonElement arguments, CancellationToken cancellationToken)
    {
        var args = arguments.Deserialize(McpJson.Default.GetSessionTitleArguments) ?? throw new McpRefused(new("bad_args"));
        return ValueTask.FromResult(gate.Read(caller, (row, scope) =>
        {
            if (args.Session != row.SessionId && !SessionDirectory.CanReach(scope, row.SessionId, args.Session))
                throw new McpRefused(new("not_reachable"));
            var machine = scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0)) ?? throw new McpRefused(new("session_gone"));
            try
            {
                if (args.Offset is null)
                {
                    if (args.Version is not null || args.Epoch is not null || args.TotalBytes is not null) throw new ArgumentException("bad_args");
                    return JsonSerializer.SerializeToElement(SessionTitleContent.Stat(scope, machine, new(args.Session)), McpJson.Default.SessionTitleStat);
                }
                return JsonSerializer.SerializeToElement(SessionTitleContent.Chunk(scope, machine, new(args.Session,
                    args.Version ?? throw new ArgumentException("bad_args"), args.Epoch ?? throw new ArgumentException("bad_args"),
                    args.TotalBytes ?? throw new ArgumentException("bad_args"), args.Offset.Value, args.Count)), McpJson.Default.SessionTitleChunk);
            }
            catch (ArgumentException) { throw new McpRefused(new("bad_args")); }
            catch (InvalidOperationException error) { throw new McpRefused(new(error.Message)); }
            catch (KeyNotFoundException) { throw new McpRefused(new("not_reachable")); }
        }, cancellationToken));
    }
}
