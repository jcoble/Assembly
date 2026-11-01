using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Messages;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Runs;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine;

public sealed record WindowCommand(Guid CommandId, string EntityId, long ExpectedVersion,
    long Epoch, EngineCommand Command,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Machine = null);
public sealed record EngineUsageArgs(string EntityKind, string EntityId, string? Machine = null);
public sealed record EngineEntityArgs(string EntityKind, string EntityId, string? Machine = null);

/// <summary>The authenticated window's seven commands; identity and time are host-owned.</summary>
public sealed class EngineCalls(EngineRuntime runtime, TimeProvider time) : IFeature
{
    private readonly MachineForward? _forward;
    internal EngineCalls(EngineRuntime runtime, TimeProvider time, MachineForward forward) : this(runtime, time) => _forward = forward;
    private MachineForward Forward => _forward ?? throw new KeyNotFoundException("Unknown remote engine machine");
    private bool IsRemote(string? machine) => machine is not null && machine != runtime.Machine;

    public void Register(ICallRegistry calls)
    {
        calls.Write("engine", "command", EngineSocketJson.Default.WindowCommand, EngineJson.Default.CommandResult,
            async (args, call) =>
            {
                if (args.Command is not (StartSession or StopSession or StartRun or CancelRun or
                    ResolveNeed { State: "answered" or "dismissed" } or SendMessage or RequestHandoff))
                    return _ => new(false, false, "forbidden", args.ExpectedVersion, 0, args.Epoch);
                if (args.CommandId == Guid.Empty || string.IsNullOrWhiteSpace(args.EntityId) || args.ExpectedVersion < 0 || args.Epoch < 0)
                    return _ => new(false, false, "bad_args", args.ExpectedVersion, 0, args.Epoch);
                if (IsRemote(args.Machine))
                {
                    var result = await Forward.CommandAsync(args.Machine!, args, call.Cancellation);
                    return _ => result;
                }
                return scope =>
                {
                    var context = new CommandContext(args.CommandId, runtime.Machine, new(ActorKind.Window, "owner"),
                        args.EntityId, args.ExpectedVersion, args.Epoch, time.GetUtcNow().ToUnixTimeMilliseconds());
                    return runtime.Commands.Apply(scope, context, args.Command);
                };
            }, order: args => IsRemote(args.Machine) ? args.Machine : null);
        calls.Read("engine", "machine", EngineSocketJson.Default.JsonElement, EngineSocketJson.Default.MachineInfo,
            (_, call) => Task.FromResult(runtime.Reader.Machine(call.Cancellation)));
        calls.Read("engine", "usage", EngineSocketJson.Default.EngineUsageArgs, EngineSocketJson.Default.UsageTotals,
            async (args, call) => IsRemote(args.Machine)
                ? (await Forward.CallAsync(args.Machine!, "engine", "usage",
                    JsonSerializer.SerializeToElement(args with { Machine = null }, EngineSocketJson.Default.EngineUsageArgs), false, call.Cancellation))
                    .Deserialize(EngineSocketJson.Default.UsageTotals)!
                : runtime.Usage.Sum(args.EntityKind, args.EntityId, call.Cancellation));
        calls.Read("engine", "entity", EngineSocketJson.Default.EngineEntityArgs, EngineSocketJson.Default.NullableJsonElement,
            async (args, call) => IsRemote(args.Machine) && args.EntityKind is "tool_call" or "usage"
                ? (await Forward.CallAsync(args.Machine!, "engine", "entity",
                    JsonSerializer.SerializeToElement(args with { Machine = null }, EngineSocketJson.Default.EngineEntityArgs), true, call.Cancellation))
                    .Deserialize(EngineSocketJson.Default.NullableJsonElement)
                : runtime.Reader.Entity(args.EntityKind, args.EntityId, call.Cancellation, args.Machine), bulk: true);
    }
}
