using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Messages;
using Assembly.Application.Features.Engine.NeedsMe;
using Assembly.Application.Features.Engine.Runs;
using Assembly.Application.Features.Engine.Sessions;

namespace Assembly.Infrastructure.Engine;

public sealed record WindowCommand(Guid CommandId, string EntityId, long ExpectedVersion,
    long Epoch, EngineCommand Command);
public sealed record EngineUsageArgs(string EntityKind, string EntityId);
public sealed record EngineEntityArgs(string EntityKind, string EntityId);

/// <summary>The authenticated window's six commands; identity and time are host-owned.</summary>
public sealed class EngineCalls(EngineRuntime runtime, TimeProvider time) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        calls.Write("engine", "command", EngineSocketJson.Default.WindowCommand, EngineJson.Default.CommandResult,
            (args, _) => Task.FromResult<Func<IWriteScope, CommandResult>>(scope =>
            {
                if (args.Command is not (StartSession or StopSession or StartRun or CancelRun or
                    ResolveNeed { State: "answered" or "dismissed" } or SendMessage))
                    return new(false, false, "forbidden", args.ExpectedVersion, 0, args.Epoch);
                if (args.CommandId == Guid.Empty || string.IsNullOrWhiteSpace(args.EntityId) || args.ExpectedVersion < 0 || args.Epoch < 0)
                    return new(false, false, "bad_args", args.ExpectedVersion, 0, args.Epoch);
                var context = new CommandContext(args.CommandId, runtime.Machine, new(ActorKind.Window, "owner"),
                    args.EntityId, args.ExpectedVersion, args.Epoch, time.GetUtcNow().ToUnixTimeMilliseconds());
                return runtime.Commands.Apply(scope, context, args.Command);
            }));
        calls.Read("engine", "usage", EngineSocketJson.Default.EngineUsageArgs, EngineSocketJson.Default.UsageTotals,
            (args, call) => Task.FromResult(runtime.Usage.Sum(args.EntityKind, args.EntityId, call.Cancellation)));
        calls.Read("engine", "entity", EngineSocketJson.Default.EngineEntityArgs, EngineSocketJson.Default.NullableJsonElement,
            (args, call) => Task.FromResult(runtime.Reader.Entity(args.EntityKind, args.EntityId, call.Cancellation)), bulk: true);
    }
}
