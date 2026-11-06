using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Agents;

namespace Assembly.Infrastructure.Agents;

/// <summary>Each helper request owns one configured ACP adapter and one empty working directory.</summary>
internal sealed class AcpUtilityModel
    : IUtilityModel
{
    private readonly IReadOnlyList<AgentDescriptor> _agents;
    private readonly Func<string?> _readUtilityModel;
    private readonly TimeProvider _time;
    private readonly TextWriter _log;
    private readonly Func<AgentCommand, IPermissionPolicy, IAgentLink> _start;

    internal AcpUtilityModel(IReadOnlyList<AgentDescriptor> agents, Func<string?> readUtilityModel, TimeProvider time, TextWriter log)
        : this(agents, readUtilityModel, time, log, (command, policy) => AgentConnection.Start(command, policy, TranscriptLog.Discard())) { }

    internal AcpUtilityModel(IReadOnlyList<AgentDescriptor> agents, Func<string?> readUtilityModel, TimeProvider time, TextWriter log,
        Func<AgentCommand, IPermissionPolicy, IAgentLink> start)
    { _agents = agents; _readUtilityModel = readUtilityModel; _time = time; _log = log; _start = start; }

    public async Task<UtilityResult> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var setting = Setting();
        if (setting == "off") return new(UtilityStatus.Unavailable, null, "utility_off");
        var descriptor = setting is null ? _agents.FirstOrDefault() : _agents.FirstOrDefault(agent => agent.Id == setting);
        if (descriptor is null || descriptor.Invalid() is not null) return new(UtilityStatus.Unavailable, null, "utility_unconfigured");
        var temp = Directory.CreateTempSubdirectory("assembly-utility-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120), _time);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        using var reading = new CancellationTokenSource();
        IAgentLink? link = null;
        string? session = null;
        Task? updates = null;
        PromptRun? run = null;
        var finished = false;
        var disposed = true;
        var result = new UtilityResult(UtilityStatus.Failed, null, "utility_failed");
        try
        {
            var permissions = new UtilityPermissions();
            link = _start(descriptor.ToCommand(temp), permissions);
            disposed = false;
            await link.Client.InitializeAsync(new("Assembly utility", "1"), lifetime.Token);
            session = (await link.Client.NewSessionAsync(new(temp, []), lifetime.Token)).SessionId;
            if (string.IsNullOrWhiteSpace(session)) throw new InvalidDataException("No utility session id.");
            var output = new AcpUtilityOutput(session);
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            updates = ReadUpdates(); // Own the channel before submitting the prompt.
            run = link.Client.PromptAsync(session, [TextBlock.Of(prompt)], lifetime.Token);
            var completed = Task.WhenAll(run.Inserted, run.Ended);
            var first = await Task.WhenAny(completed, observed.Task, link.Exited).WaitAsync(lifetime.Token);
            if (first == link.Exited) throw new IOException("The utility adapter exited.");
            if (first == observed.Task) await observed.Task;
            if (output.Failure is not null) throw new InvalidDataException(output.Failure);
            await completed.WaitAsync(lifetime.Token);
            await observed.Task.WaitAsync(lifetime.Token); // Text preceding the idle update has been consumed.
            var ended = await run.Ended;
            finished = true;
            result = permissions.Used || output.Failure == "utility_used_tools"
                ? new(UtilityStatus.Failed, null, "utility_used_tools")
                : ended.Error?.IsAuthRequired == true ? new(UtilityStatus.Unavailable, null, "utility_auth_required")
                : ended.Error is not null || ended.StopReason != "end_turn" ? new(UtilityStatus.Failed, null, "utility_failed")
                : output.Result();

            async Task ReadUpdates()
            {
                try
                {
                    await foreach (var update in link.Client.Updates.ReadAllAsync(reading.Token))
                    {
                        output.Read(update);
                        if (output.Failure is not null || output.Ended) observed.TrySetResult();
                    }
                    observed.TrySetException(new IOException("The utility update stream ended."));
                }
                catch (OperationCanceledException) when (reading.IsCancellationRequested) { }
                catch (Exception error) { observed.TrySetException(error); }
            }
        }
        catch (JsonRpcException error) when (error.Error.IsAuthRequired)
        { result = new(UtilityStatus.Unavailable, null, "utility_auth_required"); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        { result = new(UtilityStatus.Failed, null, "utility_timeout"); }
        catch (Exception error)
        {
            var reason = error is InvalidDataException && error.Message.StartsWith("utility_", StringComparison.Ordinal)
                ? error.Message : "utility_failed";
            result = new(reason == "utility_auth_required" ? UtilityStatus.Unavailable : UtilityStatus.Failed, null, reason);
            _log.WriteLine($"utility model: {reason}");
        }
        finally
        {
            if (link is not null)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(5), _time);
                if (session is not null)
                {
                    try
                    {
                        if (!finished) await link.Client.CancelAsync(session, closing.Token);
                    }
                    catch (Exception) { result = new(UtilityStatus.Failed, null, "utility_cleanup_failed"); }
                    // Closing is attempted even if cancellation failed; disposal still owns the adapter.
                    using var close = new CancellationTokenSource(TimeSpan.FromSeconds(5), _time);
                    try { await link.Client.CloseSessionAsync(session, close.Token); }
                    catch (Exception) { result = new(UtilityStatus.Failed, null, "utility_cleanup_failed"); }
                }
                reading.Cancel();
                if (updates is not null) await updates;
                try { await link.DisposeAsync(); disposed = true; }
                catch (Exception) { result = new(UtilityStatus.Failed, null, "utility_cleanup_failed"); }
                if (run is not null && disposed)
                {
                    try { await run.Inserted; await run.Ended; }
                    catch (Exception) { /* Disposal resolves the exact owned prompt's pending tasks. */ }
                }
            }
            try { if (disposed) Directory.Delete(temp, recursive: true); }
            catch (Exception) { result = new(UtilityStatus.Failed, null, "utility_cleanup_failed"); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private string? Setting()
    {
        if (_readUtilityModel() is not { } value) return null;
        using var json = JsonDocument.Parse(value);
        return json.RootElement.ValueKind == JsonValueKind.String ? json.RootElement.GetString() : "off";
    }

    private sealed class UtilityPermissions : IPermissionPolicy
    {
        public bool AllowAutomaticBoardUpdates => false;
        private int _used;
        internal bool Used => Volatile.Read(ref _used) != 0;
        public Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken cancellationToken)
        { Interlocked.Exchange(ref _used, 1); return Task.FromResult(PermissionOutcome.Cancelled); }
        public Task<ElicitationResponse> ElicitAsync(ElicitationRequest request, CancellationToken cancellationToken)
        { Interlocked.Exchange(ref _used, 1); return Task.FromResult(ElicitationResponse.Cancel); }
        public ValueTask<bool> ElicitUrlAsync(string? sessionId, Uri url, string host, CancellationToken ct)
        { Interlocked.Exchange(ref _used, 1); return ValueTask.FromResult(false); }
    }
}
