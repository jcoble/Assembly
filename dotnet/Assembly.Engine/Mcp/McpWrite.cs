using System.Globalization;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Mcp;

/// <summary>Valid only within the synchronous McpGate.Write callback. Never retain its scope or nest writes.</summary>
public sealed partial class McpWrite
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
            entityId, expectedVersion, epoch, _time.GetUtcNow().ToUnixTimeMilliseconds(), new("mcp", null, null));
        var result = _runtime.Commands.Apply(Scope, context, command);
        if (result.Applied) return result;
        IReadOnlyDictionary<string, string>? values = result.Error switch
        {
            "depth_limit" => new Dictionary<string, string>
            {
                ["depth"] = (Caller.Depth + 1L).ToString(CultureInfo.InvariantCulture),
                ["limit"] = _limits.ReadDepth().ToString(CultureInfo.InvariantCulture)
            },
            "child_limit" => new Dictionary<string, string>
            {
                ["limit"] = _limits.ReadChildren().ToString(CultureInfo.InvariantCulture)
            },
            "invalid_transition" when command is Runs.SetStepState step => new Dictionary<string, string> { ["state"] = step.State },
            _ => null
        };
        throw new McpRefused(new(ToolCode(result.Error!), values));
    }

    public CommandResult ApplyHelperPrompt(string childId, string messageId, string validatedPrompt)
    {
        var context = new CommandContext(Guid.NewGuid(), _runtime.Machine, new(ActorKind.Session, Caller.SessionId),
            messageId, 0, EpochOf(childId), _time.GetUtcNow().ToUnixTimeMilliseconds());
        var prepared = Messages.PreparedHelperPrompt.Prepare(new Messages.SendMessage(Caller.SessionId, childId, validatedPrompt, "prompt"), Caller.Title, Caller.Epoch);
        var result = _runtime.Commands.ApplyPreparedHelperPrompt(Scope, context, prepared);
        if (!result.Applied) throw new McpRefused(new(ToolCode(result.Error!)));
        return result;
    }

    private static string ToolCode(string code) => code == "message_limit" ? "pair_limit" : code;

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
