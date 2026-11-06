using Assembly.Application.Common.Database;
using Assembly.Application.Common.Calls;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine.Relays;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine;

/// <summary>One desktop coordinator path for owner-window and authenticated local-session starts.</summary>
internal sealed partial class MachineStarts
{
    private readonly EngineRuntime _runtime;
    private readonly IEngineRuntime _engine;
    private readonly MachineForward _forward;
    private readonly RemoteConnections _connections;
    private readonly SessionLimits _limits;
    private readonly TimeProvider _time;
    private readonly McpGate _gate;

    internal MachineStarts(EngineRuntime runtime, MachineForward forward, RemoteConnections connections, SessionLimits limits, TimeProvider time)
    {
        (_runtime, _engine, _forward, _connections, _limits, _time) = (runtime, runtime, forward, connections, limits, time);
        _gate = new(runtime, limits, time);
    }

    internal bool OwnsParent(string id, CancellationToken ct) => _engine.Read(scope => scope.QueryFirst("""
        SELECT COUNT(*) FROM engine_session WHERE id=$id AND machine=$machine AND state<>'foreign'
        """, row => row.Long(0), new SqlArg("$id", id), new SqlArg("$machine", _runtime.Machine)) == 1, ct);

    public async Task<CommandResult> StartAsync(CommandContext context, StartSession command, string targetMachine,
        CancellationToken ct, bool managedCheckout = false)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _runtime.Stopping);
        try
        {
            var source = _engine.Read(scope => ReadSource(scope, command.ParentId ?? throw Refused("bad_args"), null), stop.Token);
            await using var child = await StartChildAsync(context, command, targetMachine, source, null, managedCheckout, stop.Token);
            return child.Result;
        }
        catch (McpRefused error) { return new(false, false, error.Refusal.Code, context.ExpectedVersion, 0, context.Epoch); }
        catch (ForeignAncestryRefused error) { return error.Result; }
    }

    private async Task<StartedChild> StartChildAsync(CommandContext context, StartSession command, string targetMachine,
        SourceSnapshot source, McpCaller? caller, bool managedCheckout, CancellationToken ct)
    {
        if (context.Machine != _runtime.Machine || context.CommandId == Guid.Empty || !Guid.TryParse(context.EntityId, out _) ||
            context.ExpectedVersion != 0 || context.Epoch != 0 || targetMachine == _runtime.Machine || string.IsNullOrWhiteSpace(targetMachine))
            throw Refused("bad_args");
        if (command.RunId is not null || (caller is null ? context.Actor != new EngineActor(ActorKind.Window, "owner") :
                context.Actor != new EngineActor(ActorKind.Session, caller.SessionId)) || command.ParentId != source.Parent.Id)
            throw Refused("forbidden");
        if (managedCheckout && string.IsNullOrWhiteSpace(source.Parent.Repository)) throw Refused("bad_args");
        StartTitleSources? titles = await StartTitleSources.CaptureAsync(this, source, targetMachine, caller, ct);
        try
        {
            source = titles.Source;
            var target = titles.Target;
            var prepared = managedCheckout
                ? command with
                {
                    Folder = (await _forward.StartLocationAsync(target.Remote, target.Live.OwnerBinding, source.Parent.Id, ct, target.Live)).Folder,
                    RepoUrl = source.Parent.Repository,
                    ManagedCheckout = true
                }
                : command with { RepoUrl = null, ManagedCheckout = false };
            var intent = FrozenMachineStartIntent.Create(target.Remote, target.Live.OwnerBinding,
                new(context.CommandId, _runtime.Machine, context.EntityId, context.ExpectedVersion, context.Epoch, prepared), target.Live);
            while (true)
            {
                titles ??= await StartTitleSources.CaptureAsync(this, source, targetMachine, caller, ct);
                source = titles.Source; target = titles.Target;
                var lease = titles.TargetLease;
                await titles.RequireCurrentAsync(ct);
                var registered = await _forward.RegisterForeignTitleAsync(lease,
                    new(Guid.NewGuid(), _runtime.Machine, source.Parent.Id, 0, 0,
                        new RegisterForeignSession(_runtime.Machine, source.Parent.Provider, source.Parent.Title)), ct);
                RequireTitleReply(registered);
                await titles.RequireCurrentAsync(ct);
                var args = new ForeignAncestryArgs(_runtime.Machine, source.Parent.Id, source.RootToParent);
                var ancestry = await _forward.ForeignAncestryTitlesAsync(lease, args, ct);
                RequireTitleReply(ancestry, ForeignTitleDigests.Structure(targetMachine, args.FromMachine, args.ParentSessionId, args.RootToParent));
                var owners = await CapturedLiveBatch.CaptureAsync(_connections, _runtime.Machine, ct);
                if (!ReferenceEquals(owners.Target(targetMachine).Live, target.Live)) throw Refused("mirror_unavailable");
                FinalSourceCheck(source, caller, owners);
                await titles.RequireCurrentAsync(ct);
                await owners.RequireCurrentAsync(ct);
                MachineStartResolution resolution;
                var acknowledged = target;
                try { resolution = await _forward.SendStartAsync(intent, lease, ct); }
                catch (Exception) when (!ct.IsCancellationRequested && intent.Phase != StartIssuePhase.NotIssued)
                {
                    // Send has terminated. Its actual phase, rather than the exception, governs the probe.
                    // A bulk failure may leave the normal binding live while invalidating captured source handles.
                    var old = titles; titles = null;
                    await old.DisposeAsync();
                    var current = await WaitForTargetAsync(target, ct);
                    await using var probeLease = await current.Remote.AcquireRelayBulkAsync(current.Live.OwnerBinding, ct, current.Live);
                    resolution = await _forward.ProbeStartAsync(intent, current.Remote, current.Live.OwnerBinding, probeLease, ct, current.Live);
                    acknowledged = current;
                }
                if (resolution.Evidence.State == MachineStartAckState.Missing &&
                    resolution.MissingProof is StartMissingProof.NotIssued or StartMissingProof.SameOriginalOrderedBarrier)
                {
                    // Only proved absence permits another admission, with the same frozen Start and new current census.
                    if (titles is { } old) { titles = null; await old.DisposeAsync(); }
                    continue;
                }
                var result = RequireStartAcknowledgement(intent, resolution);
                // A matching accepted receipt is resolved before mutable source admission checks; its own child is not new capacity.
                if (result.Applied)
                {
                    if (titles is { } captured)
                    {
                        try { captured.TargetLease.StartTransport(); }
                        catch (CallFailed) { titles = null; await captured.DisposeAsync(); }
                    }
                    titles ??= await StartTitleSources.CaptureAsync(this, source, targetMachine, caller, ct);
                    if (!ReferenceEquals(titles.Target.Live, acknowledged.Live) || !ReferenceEquals(titles.Target.Remote, acknowledged.Remote))
                        throw new CallFailed("Acknowledged target owner changed", "unknown");
                }
                return new(result, titles, intent);
            }
        }
        catch { if (titles is not null) await titles.DisposeAsync(); throw; }
    }

    private static async Task<CapturedMirror> WaitForTargetAsync(CapturedMirror target, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, target.Mirror.Stopping);
        while (true)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void CaughtUp() => changed.TrySetResult();
            target.Mirror.CaughtUp += CaughtUp;
            try
            {
                if (await target.Mirror.CaptureLiveBindingAsync(stop.Token) is { } live)
                {
                    if (live.Machine != target.Live.Machine) throw new CallFailed("Target engine identity changed", "unknown");
                    return new(target.Remote, target.Mirror, live);
                }
                await changed.Task.WaitAsync(stop.Token);
            }
            finally { target.Mirror.CaughtUp -= CaughtUp; }
        }
    }

    private static CommandResult RequireStartAcknowledgement(FrozenMachineStartIntent intent, MachineStartResolution resolution)
    {
        var evidence = resolution.Evidence;
        if (evidence.State == MachineStartAckState.Refused && evidence.Recorded is { Applied: false } refused) return refused;
        if (evidence.State == MachineStartAckState.Conflict) throw new CallFailed("Start identity conflicts with its original receipt", "unknown");
        if (evidence.State != MachineStartAckState.Accepted || evidence.Recorded is not { Applied: true } result ||
            evidence.Child is not { } child || intent.Command.Command is not StartSession original ||
            child.Id != intent.Command.EntityId || child.Machine != intent.Binding.Machine || child.Holder != "local" ||
            child.Version < 1 || child.Epoch != intent.Command.Epoch || child.ReservedEpoch is not null || child.CancelRequested ||
            child.State is "foreign" or "ended" or "lost" or "handing_off" || child.ParentId != original.ParentId || child.RunId != original.RunId)
            throw new CallFailed("Start acknowledgement or current child ownership is unknown", "unknown");
        return result;
    }

    private static void RequireTitleReply(ForeignTitleReply reply, string? structure = null)
    {
        if (structure is not null && reply.Kind == ForeignTitleReplyKind.ExistingStructure && reply.VerifiedStructureSha256 == structure) return;
        if (reply.Kind is ForeignTitleReplyKind.Applied or ForeignTitleReplyKind.RecordedCanonical && reply.Result is { } result)
        {
            if (!result.Applied) throw Refused(result.Error!);
            return;
        }
        throw new CallFailed("Foreign title outcome is unknown", "unknown");
    }

    private sealed record StartedChild(CommandResult Result, StartTitleSources? Titles, FrozenMachineStartIntent Intent) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Titles?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    internal async Task<StartSessionAnswer> StartMcpAsync(McpCaller caller, StartSessionArguments arguments, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _runtime.Stopping);
        if (arguments.Prompt is null || arguments.Title is null || arguments.Machine is null) throw Refused("bad_args");
        var source = _engine.Read(scope => ReadSource(scope, caller.SessionId, caller), stop.Token);
        var provider = arguments.Provider ?? source.Parent.Provider;
        var model = arguments.Model ?? (provider == source.Parent.Provider ? source.Parent.Model : null);
        var childId = Guid.NewGuid().ToString();
        var messageId = Guid.NewGuid().ToString();
        var context = new CommandContext(Guid.NewGuid(), _runtime.Machine, new(ActorKind.Session, caller.SessionId), childId, 0, 0, Now);
        var promptCommandId = Guid.NewGuid();
        try
        {
            await using var child = await StartChildAsync(context, new(provider, model, arguments.Folder ?? "", arguments.Title,
                caller.SessionId, null, arguments.TaskUrl), arguments.Machine, source, caller, arguments.Folder is null, stop.Token);
            if (!child.Result.Applied) throw Refused(child.Result.Error!);
            var childTitles = child.Titles ?? throw new InvalidOperationException("Accepted Start lost its owned title sources.");
            source = childTitles.Source;
            // The display-derived preface is produced only after actual child acceptance.
            var currentCaller = _gate.Read(caller, (row, _) => row, stop.Token);
            var prompt = $"Session \"{currentCaller.Title}\" ({caller.SessionId}) started you. Do the task below. Your last reply each turn goes back to it as a message; you can also message it with the Assembly MCP server's send_message tool.\n\n{arguments.Prompt}";
            var preparedPrompt = PreparedHelperPrompt.Prepare(new SendMessage(caller.SessionId, childId, prompt, "prompt"), currentCaller.Title, caller.Epoch);
            var retainedPrompt = new RetainedInitialPrompt(
                new(promptCommandId, _runtime.Machine, new(ActorKind.Session, caller.SessionId), messageId, 0, 0, Now), preparedPrompt);
            var chain = source.RootToParent.Append(new ForeignAncestor(childId, arguments.Machine, provider, arguments.Title, caller.SessionId)).ToArray();
            var childAncestry = _runtime.Commands.PrepareForeignAncestry(new(arguments.Machine, childId, chain));
            await childTitles.RequireCurrentAsync(stop.Token);
            var target = childTitles.Target;
            var currentStart = RequireStartAcknowledgement(child.Intent, await _forward.ProbeStartAsync(child.Intent,
                target.Remote, target.Live.OwnerBinding, childTitles.TargetLease, stop.Token, target.Live));
            if (!currentStart.Applied) throw Refused(currentStart.Error!);
            stop.Token.ThrowIfCancellationRequested();
            CommandResult committed;
            try
            {
                committed = _engine.Write(scope =>
                {
                    if (retainedPrompt.ReplayExisting(scope, _runtime.Commands) is { } existing) return existing;
                    RevalidateSource(scope, source, caller);
                    // Actual target acceptance authorizes this reference; keep links and the initial M atomic.
                    _runtime.Commands.ApplyForeignAncestryBatch(scope, childAncestry);
                    var result = retainedPrompt.Apply(scope, _runtime.Commands);
                    if (!result.Applied) throw Refused(result.Error!);
                    return result;
                });
            }
            catch (Exception primary)
            {
                CommandResult? existing = null;
                try { existing = _engine.Write(scope => retainedPrompt.ReplayExisting(scope, _runtime.Commands)); }
                catch (Exception checkFailure) { primary.Data["MachineStarts.ReplayCheckFailed"] = checkFailure; }
                if (existing is null) throw; // No receipt is Unknown, never permission for another fresh Apply.
                committed = existing;
            }
            if (!committed.Applied) throw Refused(committed.Error!);
            return new(childId, messageId);
        }
        catch (McpRefused error) when (error.Refusal.Values is null && error.Refusal.Code is "depth_limit" or "child_limit")
        {
            throw new McpRefused(new(error.Refusal.Code, new Dictionary<string, string>
            {
                ["depth"] = source.Structure.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["limit"] = (error.Refusal.Code == "depth_limit" ? _limits.ReadDepth() : _limits.ReadChildren()).ToString(System.Globalization.CultureInfo.InvariantCulture)
            }));
        }
        catch (ForeignAncestryRefused error) { throw Refused(error.Result.Error!); }
        catch (CallFailed error)
        {
            throw new McpRefused(new("remote_start_failed", new Dictionary<string, string> { ["message"] = error.Message }));
        }
    }

    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();
}
