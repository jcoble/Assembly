using Assembly.Infrastructure.Engine.Workflows;
using Assembly.Engine.Workflows;
using Assembly.Engine.Workflows.Runs;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Engine;
using Assembly.Infrastructure.Socket;
using Assembly.Infrastructure.Engine.Needs;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Messages;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Runs;
using Assembly.Engine.Sessions;
using Assembly.Engine.Providers;
using Assembly.Engine.Reading;

namespace Assembly.Infrastructure.Engine;

public sealed record WindowCommand(Guid CommandId, string EntityId, long ExpectedVersion,
    long Epoch, EngineCommand Command,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Machine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Surface = null);
public sealed record EngineUsageArgs(string EntityKind, string EntityId, string? Machine = null);
public sealed record EngineEntityArgs(string EntityKind, string EntityId, string? Machine = null);
public sealed record EngineEntitiesArgs(IReadOnlyList<EngineEntityRef> Refs, string? After = null, string? Machine = null);

/// <summary>The authenticated window's checked owner commands; identity and time are host-owned.</summary>
public sealed class EngineCalls(EngineRuntime runtime, TimeProvider time, NeedAnswers needs, IEngineDoor? door = null, WorkflowCommandRecovery? workflowRecovery = null) : IFeature
{
    private readonly MachineForward? _forward;
    private readonly MachineStarts? _starts;
    internal EngineCalls(EngineRuntime runtime, TimeProvider time, NeedAnswers needs, MachineForward forward, IEngineDoor? door = null, WorkflowCommandRecovery? workflowRecovery = null) : this(runtime, time, needs, door, workflowRecovery) => _forward = forward;
    internal EngineCalls(EngineRuntime runtime, TimeProvider time, NeedAnswers needs, MachineForward forward, MachineStarts starts, IEngineDoor? door = null, WorkflowCommandRecovery? workflowRecovery = null) : this(runtime, time, needs, forward, door, workflowRecovery) => _starts = starts;
    private static CallFailed SourceUnavailable() => new("Source-bound pending reads are unavailable.", "source_changed");
    private MachineForward Forward => _forward ?? throw new KeyNotFoundException("Unknown remote engine machine");
    private bool IsRemote(string? machine) => machine is not null && machine != runtime.Machine;

    private static int OwnerPageBytes(long id, OwnerPromptPage page) => SocketDispatcher.Result(id,
        JsonSerializer.SerializeToElement(page, EngineSocketJson.Default.OwnerPromptPage)).Length;
    private static int EntitiesBytes(long id, EngineEntitiesPage page) => SocketDispatcher.Result(id,
        JsonSerializer.SerializeToElement(page, EngineSocketJson.Default.EngineEntitiesPage)).Length;

    public void Register(ICallRegistry calls)
    {
        calls.Write("engine", "command", EngineSocketJson.Default.WindowCommand, EngineJson.Default.CommandResult,
            async (args, call) =>
            {
                if (args.Surface is not (null or "rail" or "board") || call.Peer is PhonePeer && args.Surface is not null)
                    return _ => new(false, false, "bad_args", args.ExpectedVersion, 0, args.Epoch);
                if (call.Peer is PhonePeer && args.Machine is not null)
                    return _ => new(false, false, "forbidden", args.ExpectedVersion, 0, args.Epoch);
                var origin = call.Peer switch
                {
                    PhonePeer phone when !string.IsNullOrWhiteSpace(phone.DeviceId) => new CommandOrigin("phone", phone.DeviceId, null),
                    OwnerPeer => new CommandOrigin("desktop", null, args.Surface),
                    _ => null
                };
                if (origin is null) return _ => new(false, false, "forbidden", args.ExpectedVersion, 0, args.Epoch);
                if (args.Command is not (StartSession or StopSession or ChangeCheckout or StartRun or CancelRun or
                    PauseWorkflow or ResumeWorkflow or RetryWorkflowStep or DecideWorkflowStep or KeepWorkflowSucceeded or UndoWorkflowKeep or GrantWorkflowScope or SetWorkflowMute or RerunWorkflow or
                    ResolveNeed { State: "answered" or "dismissed" } or SendMessage or RequestHandoff or CheckProvider or StartSignIn or CancelSignIn))
                    return _ => new(false, false, "forbidden", args.ExpectedVersion, 0, args.Epoch);
                if (call.Peer is PhonePeer && args.Command is GrantWorkflowScope or DecideWorkflowStep)
                    return _ => new(false, false, "forbidden", args.ExpectedVersion, 0, args.Epoch);
                if (args.Command is CheckProvider or StartSignIn or CancelSignIn && string.IsNullOrWhiteSpace(args.Machine))
                    return _ => new(false, false, "wrong_machine", args.ExpectedVersion, 0, args.Epoch);
                if (args.Command is StartSession { RepoUrl: not null } or StartSession { ManagedCheckout: true })
                    return _ => new(false, false, "forbidden", args.ExpectedVersion, 0, args.Epoch);
                if (args.CommandId == Guid.Empty || string.IsNullOrWhiteSpace(args.EntityId) || args.ExpectedVersion < 0 || args.Epoch < 0)
                    return _ => new(false, false, "bad_args", args.ExpectedVersion, 0, args.Epoch);
                if (IsRemote(args.Machine))
                {
                    if (_starts is not null && args.Command is StartSession { ParentId: { } parent } remoteStart && _starts.OwnsParent(parent, call.Cancellation))
                    {
                        var context = new CommandContext(args.CommandId, runtime.Machine, new(ActorKind.Window, "owner"),
                            args.EntityId, args.ExpectedVersion, args.Epoch, time.GetUtcNow().ToUnixTimeMilliseconds(), origin);
                        var started = await _starts.StartAsync(context, remoteStart, args.Machine!, call.Cancellation);
                        return _ => started;
                    }
                    var result = await Forward.CommandAsync(args.Machine!, args, call.Cancellation);
                    return _ => result;
                }
                if (args.Command is ResolveNeed { State: "answered" })
                    return await needs.PrepareOwnerAsync(args, call.Cancellation, origin);
                if (args.Command is RerunWorkflow rerun)
                {
                    PreparedWorkflowCommand prepared;
                    try
                    {
                        prepared = ((Assembly.Engine.Hosting.IEngineRuntime)runtime).Read(scope => WorkflowStarts.PrepareRerun(scope,
                            new(args.CommandId, runtime.Machine, new(ActorKind.Window, "owner"), args.EntityId, args.ExpectedVersion, args.Epoch, 0, origin), rerun, call.Cancellation), call.Cancellation);
                    }
                    catch (ArgumentException error) { return _ => new(false, false, error.Message, args.ExpectedVersion, 0, args.Epoch); }
                    return scope => runtime.Commands.ApplyPreparedWorkflow(scope,
                        new(args.CommandId, runtime.Machine, new(ActorKind.Window, "owner"), args.EntityId, args.ExpectedVersion, args.Epoch, time.GetUtcNow().ToUnixTimeMilliseconds(), origin), prepared);
                }
                if (args.Command is RetryWorkflowStep or DecideWorkflowStep { Decision: "retry" })
                {
                    var retry = await WorkflowControlRetryPreparation.PrepareAsync(runtime, time, workflowRecovery, args, origin, call.Cancellation);
                    if (retry is not null) return retry;
                }
                var title = args.Command is StartSession start ? EngineCommands.PrepareSessionTitle(start) : null;
                return scope =>
                {
                    var context = new CommandContext(args.CommandId, runtime.Machine, new(ActorKind.Window, "owner"),
                        args.EntityId, args.ExpectedVersion, args.Epoch, time.GetUtcNow().ToUnixTimeMilliseconds(), origin);
                    return title is null ? runtime.Commands.Apply(scope, context, args.Command) : runtime.Commands.ApplyPreparedSessionTitle(scope, context, title);
                };
            }, order: args => IsRemote(args.Machine) ? args.Machine : null);
        calls.Read("engine", "owner_prompts", EngineSocketJson.Default.OwnerPromptReadArgs, EngineSocketJson.Default.OwnerPromptPage,
            async (args, call) =>
            {
                if (call.ReplyId is not { } replyId) throw new CallFailed("reply metadata unavailable");
                if (args.ExpectedSource is not null || call.ExpectedSource is not null)
                    return await (door ?? throw SourceUnavailable()).ReadSourceOwnerPromptsAsync(args, call);
                if (IsRemote(args.Machine))
                    return await Forward.OwnerPromptsAsync(args.Machine!, args, candidate => OwnerPageBytes(replyId, candidate), call.Cancellation);
                return runtime.Reader.OwnerPrompts(args, candidate => SocketDispatcher.Result(replyId,
                    JsonSerializer.SerializeToElement(candidate, EngineSocketJson.Default.OwnerPromptPage)).Length, call.Cancellation);
            });
        calls.Read("engine", "owner_prompt_chunk", EngineSocketJson.Default.OwnerPromptChunkArgs, EngineSocketJson.Default.OwnerPromptChunk,
            async (args, call) =>
            {
                if (args.ExpectedSource is not null || call.ExpectedSource is not null)
                    return await (door ?? throw SourceUnavailable()).ReadSourceOwnerPromptChunkAsync(args, call);
                return IsRemote(args.Machine) ? (await Forward.CallAsync(args.Machine!, "engine", "owner_prompt_chunk",
                    JsonSerializer.SerializeToElement(args with { Machine = null }, EngineSocketJson.Default.OwnerPromptChunkArgs),
                    true, call.Cancellation)).Deserialize(EngineSocketJson.Default.OwnerPromptChunk)!
                    : runtime.Reader.OwnerPromptContent(args, call.Cancellation);
            }, bulk: true);
        calls.Read("engine", "machine", EngineSocketJson.Default.JsonElement, EngineSocketJson.Default.MachineInfo,
            (_, call) => Task.FromResult(runtime.Reader.Machine(call.Cancellation)));
        calls.Read("engine", "usage", EngineSocketJson.Default.EngineUsageArgs, EngineSocketJson.Default.UsageTotals,
            async (args, call) => IsRemote(args.Machine)
                ? (await Forward.CallAsync(args.Machine!, "engine", "usage",
                    JsonSerializer.SerializeToElement(args with { Machine = null }, EngineSocketJson.Default.EngineUsageArgs), false, call.Cancellation))
                    .Deserialize(EngineSocketJson.Default.UsageTotals)!
                : runtime.Usage.Sum(args.EntityKind, args.EntityId, call.Cancellation));
        calls.Read("engine", "entity", EngineSocketJson.Default.EngineEntityArgs, EngineSocketJson.Default.NullableJsonElement,
            async (args, call) =>
            {
                if (args.EntityKind == "pr_observation" && call.Peer is not OwnerPeer) throw new CallFailed("forbidden");
                return IsRemote(args.Machine) && args.EntityKind is "tool_call" or "usage" or "pr_observation" or "workflow_settings"
                ? (await Forward.CallAsync(args.Machine!, "engine", "entity",
                    JsonSerializer.SerializeToElement(args with { Machine = null }, EngineSocketJson.Default.EngineEntityArgs), true, call.Cancellation))
                    .Deserialize(EngineSocketJson.Default.NullableJsonElement)
                : runtime.Reader.Entity(args.EntityKind, args.EntityId, call.Cancellation, args.Machine);
            }, bulk: true);
        calls.Read("engine", "entities", EngineSocketJson.Default.EngineEntitiesArgs, EngineSocketJson.Default.EngineEntitiesPage,
            async (args, call) =>
            {
                if (call.Peer is not OwnerPeer) throw new CallFailed("forbidden");
                if (call.ReplyId is not { } replyId) throw new CallFailed("reply metadata unavailable");
                if (args.Refs is null) throw new CallFailed("bad_args");
                if (IsRemote(args.Machine) && args.Refs.Any(item => item?.Kind is "tool_call" or "usage" or "workflow_settings"))
                {
                    // A mixed owner-only batch is read once on its actual owner, never split across snapshots.
                    var result = await Forward.CallAsync(args.Machine!, "engine", "entities",
                        JsonSerializer.SerializeToElement(args with { Machine = null }, EngineSocketJson.Default.EngineEntitiesArgs),
                        true, call.Cancellation);
                    var page = result.Deserialize(EngineSocketJson.Default.EngineEntitiesPage)
                        ?? throw new IOException("Invalid entity page");
                    if (EntitiesBytes(replyId, page) > 1024 * 1024) throw new CallFailed("entity response exceeds budget", "oversize");
                    return page;
                }
                try
                {
                    return runtime.Reader.Entities(args.Refs, args.After, call.Cancellation, args.Machine, call.Connection,
                        page => EntitiesBytes(replyId, page));
                }
                catch (ArgumentException error) { throw new CallBadArguments(error.Message); }
                catch (InvalidOperationException error) when (error.Message is
                    "Display item exceeds the 32 KiB UTF-8 budget." or "Entity response exceeds the 1 MiB UTF-8 budget.")
                { throw new CallFailed(error.Message, "oversize"); }
            }, bulk: true);
    }
}
