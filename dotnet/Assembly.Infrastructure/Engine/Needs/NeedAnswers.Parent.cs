using Assembly.Engine;
using System.Threading.Channels;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Mcp;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Relays;

namespace Assembly.Infrastructure.Engine.Needs;

public sealed partial class NeedAnswers
{
    private readonly Lock _waitGate = new();
    private readonly CancellationTokenSource _waitStop = new();
    private readonly CancellationToken _runtimeStopping = runtime.Stopping;
    private readonly HashSet<Task> _requests = [];
    private Task? _stopWaiting;

    public Task<CommandResult> ApplyAsync(McpCaller caller, ParentNeedRequest request,
        CancellationToken workCancellation, CancellationToken waitCancellation)
    {
        var operation = Guid.NewGuid();
        request = request with { Answer = request.Answer?.Clone() };
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(workCancellation, _runtimeStopping);
        admission.Token.ThrowIfCancellationRequested();
        var target = gate.Read(caller, (_, scope) => ReadLocalTarget(scope, request.NeedId), admission.Token);
        var source = target is null ? runtime.Commands.PrepareParentNeedIntent(operation, caller, request, admission.Token) : null;
        TaskCompletionSource ready; Task<CommandResult> result;
        lock (_waitGate)
        {
            if (_stopWaiting is not null) throw new OperationCanceledException("Need answer waits stopped.");
            _requests.RemoveWhere(task => task.IsCompleted);
            ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            result = ApplyAfter(ready.Task, operation, caller, request, target, source, workCancellation, waitCancellation);
            _requests.Add(result);
        }
        ready.SetResult(); return result;
    }

    private async Task<CommandResult> ApplyAfter(Task ready, Guid operation, McpCaller caller, ParentNeedRequest request,
        NeedAnswerTarget? target, PreparedParentNeedIntent? source, CancellationToken workCancellation, CancellationToken waitCancellation)
    {
        await ready.ConfigureAwait(false);
        using var work = CancellationTokenSource.CreateLinkedTokenSource(workCancellation, _runtimeStopping);
        var ct = work.Token; ct.ThrowIfCancellationRequested();
        if (target is not null)
        {
            ProviderAnswerBinding? binding = null;
            if (request.Action == ParentNeedAction.Answer && target.ProviderBacked)
            {
                if (request.Answer is not { } answer || target.ChatRowId is null)
                    throw new McpRefused(new("bad_answer"));
                var captured = sessions.For(target.ChildSessionId).PrepareProviderAnswer(target.ChatRowId, answer)
                    ?? throw new McpRefused(new("bad_answer"));
                binding = captured.Binding; request = request with { Answer = captured.Answer };
            }
            var prepared = EngineCommands.PrepareParentNeed(operation, request, target, binding, ct);
            ct.ThrowIfCancellationRequested();
            return gate.Write(caller, write => runtime.Commands.ApplyLocalParentNeed(write.Scope, caller, prepared,
                time.GetUtcNow().ToUnixTimeMilliseconds()));
        }
        ct.ThrowIfCancellationRequested();
        var accepted = gate.Write(caller, write => runtime.Commands.BeginParentNeedIntent(write.Scope, caller, source!,
            time.GetUtcNow().ToUnixTimeMilliseconds()));
        if (!accepted.Result.Applied) throw new McpRefused(new(accepted.Result.Error!));
        return await WaitForOutcome(accepted.IntentId, source!, waitCancellation).ConfigureAwait(false);
    }

    private NeedAnswerTarget? ReadLocalTarget(IReadScope scope, string needId) => scope.QueryFirst("""
        SELECT n.id,n.version,n.session_id,s.machine,s.epoch,s.holder,s.reserved_epoch,n.kind,n.provider_request_id,
            s.parent_id,p.machine,n.addressee_session_id,n.chat_row_id
        FROM engine_needs_me n JOIN engine_session s ON s.id=n.session_id
            LEFT JOIN engine_session p ON p.id=s.parent_id
        WHERE n.id=$id AND s.machine=$machine AND s.state<>'foreign'
        """, r => new NeedAnswerTarget(r.Text(0), r.Long(1), r.Text(2), r.Text(3), r.Long(4), r.Text(5),
            r.IsNull(6) ? null : r.Long(6), r.Text(7), !r.IsNull(8), r.IsNull(9) ? "" : r.Text(9),
            r.IsNull(10) ? "" : r.Text(10), r.IsNull(11) ? null : r.Text(11), r.IsNull(8) ? null : r.Text(8),
            r.IsNull(12) ? null : r.Text(12)), new SqlArg("$id", needId), new("$machine", runtime.Machine));

    internal ParentNeedCaptured CaptureParentNeed(ParentNeedSource source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (source.SourceMachine == runtime.Machine || source.OperationId == Guid.Empty || !Enum.IsDefined(source.Action))
            throw new RelayRefused("bad_args");
        var target = ((IEngineRuntime)runtime).Read(scope => ReadLocalTarget(scope, source.NeedId), ct);
        if (target is null || target.ParentSessionId != source.ParentSessionId || target.ParentOwnerMachine != source.SourceMachine ||
            target.AddresseeSessionId != source.ParentSessionId || target.ChildOwnerMachine != runtime.Machine)
            return new(null, null, "bad_args");
        ProviderAnswerBinding? binding = null;
        if (source.Action == ParentNeedAction.Answer && target.ProviderBacked)
        {
            if (target.ChatRowId is null) return new(null, null, "bad_args");
            binding = sessions.For(target.ChildSessionId).CaptureProviderAnswerBinding(target.ChatRowId);
            if (binding is null) return new(null, null, "request_gone");
        }
        return new(target, binding, null);
    }

    internal PreparedForwardedParentNeed PrepareForwardedParentNeed(ParentNeedPrepared payload, CancellationToken ct)
    {
        var descriptor = payload.Descriptor;
        if (descriptor.Target.ChildOwnerMachine != runtime.Machine) throw new RelayRefused("wrong_machine");
        var prepared = PreparedParentNeed.Receive(descriptor, payload.AnswerUtf8, ct);
        var error = ParentNeedPreparationError.None;
        if (descriptor.Source.Action == ParentNeedAction.Answer && descriptor.Target.ProviderBacked)
        {
            var host = sessions.For(descriptor.Target.ChildSessionId);
            if (descriptor.Target.ChatRowId is not { } chat || descriptor.ProviderBinding is not { } binding ||
                host.CaptureProviderAnswerBinding(chat) != binding) error = ParentNeedPreparationError.RequestGone;
            else if (prepared.Request.Answer is not { } answer || host.PrepareProviderAnswer(chat, answer) is not { } current ||
                current.Binding != binding) error = ParentNeedPreparationError.BadAnswer;
        }
        return PreparedForwardedParentNeed.Prepare(descriptor.Source, descriptor.Target, prepared, error);
    }

    private async Task<CommandResult> WaitForOutcome(string intentId, PreparedParentNeedIntent source, CancellationToken waitCancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(waitCancellation, _runtimeStopping, _waitStop.Token);
        var ct = linked.Token;
        var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        void Signal() => wake.Writer.TryWrite(true);
        using var registration = runtime.Publisher.Register(item =>
        {
            if (item.EntityKind == "effect" && item.EntityId == intentId) Signal();
        }, out _, Signal);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = runtime.Reader.ParentNeedOutcome(intentId, source.Payload.Source, source.Payload.InputSha256, ct);
            if (outcome is not null)
            {
                if (outcome.Origin == "source") throw new McpRefused(new(outcome.Error ?? "bad_args"));
                return outcome.Result ?? throw new RelayRefused("source_changed");
            }
            await wake.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
    }

    public Task StopWaiting()
    {
        TaskCompletionSource? ready = null; Task result;
        lock (_waitGate)
        {
            if (_stopWaiting is null)
            {
                ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopWaiting = StopWaitingAfter(ready.Task, _requests.ToArray());
            }
            result = _stopWaiting;
        }
        ready?.SetResult(); return result;
    }

    private async Task StopWaitingAfter(Task ready, Task[] requests)
    {
        await ready.ConfigureAwait(false);
        try { await _waitStop.CancelAsync().ConfigureAwait(false); }
        finally
        {
            try { await Task.WhenAll(requests).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (McpRefused) { }
            finally { _waitStop.Dispose(); }
        }
    }

    public ValueTask DisposeAsync() => new(StopWaiting());
}
