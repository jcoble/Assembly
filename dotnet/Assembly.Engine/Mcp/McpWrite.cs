using System.Globalization;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Mcp;

/// <summary>Valid only within the synchronous McpGate.Write callback. Never retain its scope or nest writes.</summary>
public sealed class McpWrite
{
    private readonly IEngineRuntime _runtime;
    private readonly SessionLimits _limits;
    private readonly TimeProvider _time;
    public CallerRow Caller { get; }
    public IWriteScope Scope { get; }

    internal McpWrite(CallerRow caller, IWriteScope scope, IEngineRuntime runtime, SessionLimits limits, TimeProvider time)
    {
        Caller = caller;
        Scope = scope;
        _runtime = runtime;
        _limits = limits;
        _time = time;
    }

    public CommandResult Apply(string entityId, long expectedVersion, long epoch, EngineCommand command)
    {
        var context = new CommandContext(Guid.NewGuid(), _runtime.Machine, new(ActorKind.Session, Caller.SessionId),
            entityId, expectedVersion, epoch, _time.GetUtcNow().ToUnixTimeMilliseconds());
        var result = _runtime.Commands.Apply(Scope, context, command);
        if (result.Applied) return result;
        IReadOnlyDictionary<string, string>? values = result.Error switch
        {
            "depth_limit" => new Dictionary<string, string>
            {
                ["depth"] = (Caller.Depth + 1).ToString(CultureInfo.InvariantCulture),
                ["limit"] = Math.Clamp(_limits.Depth(), 1, 3).ToString(CultureInfo.InvariantCulture)
            },
            "child_limit" => new Dictionary<string, string>
            {
                ["limit"] = Math.Clamp(_limits.Children(), 1, 10).ToString(CultureInfo.InvariantCulture)
            },
            "invalid_transition" when command is Runs.SetStepState step => new Dictionary<string, string> { ["state"] = step.State },
            _ => null
        };
        throw new McpRefused(new(result.Error!, values));
    }

    public long VersionOf(string entityKind, string id)
    {
        var table = entityKind switch
        {
            "session" => "engine_session",
            "run" => "engine_run",
            "step" => "engine_step",
            "needs_me" => "engine_needs_me",
            "message" => "engine_message",
            "handoff" => "engine_handoff",
            "pr" => "engine_pr",
            "tool_call" => "engine_tool_call",
            "usage" => "engine_usage",
            _ => throw new ArgumentOutOfRangeException(nameof(entityKind), entityKind, "Unknown engine entity kind.")
        };
        return Scope.QueryFirst($"SELECT version FROM {table} WHERE id=$id", r => r.Long(0), new SqlArg("$id", id));
    }

    public long EpochOf(string sessionId) => Scope.QueryFirst("SELECT epoch FROM engine_session WHERE id=$id",
        r => (long?)r.Long(0), new SqlArg("$id", sessionId)) ?? throw new McpRefused(new("not_reachable"));
}
