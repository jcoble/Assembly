using System.ComponentModel;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;

namespace Assembly.Application.Common.Agents;

public sealed partial class SharedAdapter
{
    private readonly CancellationTokenSource _providerStop = new();
    private readonly object _providerWorkGate = new();
    private readonly List<Task> _providerWork = [];

    internal Task<ProviderProbeResult> ProbeAsync(AgentDescriptor descriptor, string targetId, string probeFolder, CancellationToken ct)
    {
        var task = ProbeCoreAsync(descriptor, targetId, probeFolder, ct);
        OwnProviderWork(task);
        return task;
    }

    internal async Task<RunningAdapter?> CaptureAsync(string targetId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _running is { } running && !running.Link.Exited.IsCompleted && running.TargetId == targetId ? running : null;
        }
        finally { _gate.Release(); }
    }

    private async Task<ProviderProbeResult> ProbeCoreAsync(AgentDescriptor descriptor, string targetId, string probeFolder, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _providerStop.Token);
        ct = stop.Token;
        RunningAdapter? running = null;
        IAgentLink? observed = null;
        AuthStatus? auth = null;
        InitializeResponse? initialize = null;
        NewSessionResponse? session = null;
        string? problem = null;
        var required = false;
        var cancelled = false;
        void Status(AuthStatus value) => Volatile.Write(ref auth, value);
        void Observe(IAgentLink link)
        {
            observed = link;
            link.Client.AuthStatusChanged += Status;
        }
        try
        {
            running = await GetForSessionAsync(null, new StageTimes(time), ct, targetId, Observe, descriptor);
            initialize = running.Init;
            // A cancelled open is still owned: the returned id must be closed, even after the caller stops waiting.
            var opening = running.Link.Client.NewSessionAsync(new(probeFolder, []), CancellationToken.None);
            try { session = await opening.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                cancelled = true;
                try { session = await opening.WaitAsync(CloseLimit, time, CancellationToken.None); }
                catch (TimeoutException)
                {
                    await RetireAsync(running, null);
                    OwnProviderWork(FinishLateProbeAsync(running, opening));
                }
                catch (Exception) { /* The observed open failed and created no session. */ }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled = true; }
        catch (JsonRpcException error) when (error.Error.IsAuthRequired) { required = true; }
        catch (Exception error)
        {
            problem = error is Win32Exception { NativeErrorCode: 2 } ? "not_installed"
                : observed?.Exited.IsCompleted == true ? "adapter_crashed"
                : observed is null ? "cannot_start" : "adapter_error";
        }
        finally
        {
            if (observed is not null) observed.Client.AuthStatusChanged -= Status;
            if (running is not null)
            {
                if (session is not null && !await CloseProbeAsync(running, session.SessionId)) problem ??= "adapter_error";
                await ReleaseAsync(running);
            }
        }
        if (cancelled) throw new OperationCanceledException(ct);
        return new(initialize, session, Volatile.Read(ref auth), required, problem);
    }

    private async Task<bool> CloseProbeAsync(RunningAdapter running, string sessionId)
    {
        running.Updates.Forget(sessionId);
        try
        {
            await CloseAsync(running, sessionId);
            return true;
        }
        catch (Exception)
        {
            // Keep active borrowers alive; retire only this exact instance through the shared owner.
            await RetireAsync(running, sessionId);
            return false;
        }
    }

    private async Task FinishLateProbeAsync(RunningAdapter running, Task<NewSessionResponse> opening)
    {
        try
        {
            var late = await opening;
            await CloseProbeAsync(running, late.SessionId);
        }
        catch (Exception) { /* The exact owning link ended without returning a session id. */ }
    }

    private void OwnProviderWork(Task task)
    {
        lock (_providerWorkGate)
        {
            _providerWork.Add(task);
            var observer = task.ContinueWith(completed =>
            {
                _ = completed.Exception; // Observe failure without retaining or logging private probe material.
                lock (_providerWorkGate) _providerWork.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _providerWork.RemoveAll(item => item.IsCompleted);
            _providerWork.Add(observer);
        }
    }

    private async Task DrainProviderWorkAsync()
    {
        while (true)
        {
            Task[] owned;
            lock (_providerWorkGate)
            {
                owned = _providerWork.Where(task => !task.IsCompleted).ToArray();
                if (owned.Length == 0)
                {
                    _providerWork.Clear();
                    return;
                }
            }
            try { await Task.WhenAll(owned); }
            catch (OperationCanceledException) { }
        }
    }
}
