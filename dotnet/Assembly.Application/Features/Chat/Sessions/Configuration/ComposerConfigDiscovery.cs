using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions.Configuration;

/// <summary>A private, bounded metadata probe. Concurrent drafts share a probe for the same inputs.</summary>
public sealed class ComposerConfigDiscovery(
    IAgentLauncher launcher, TimeProvider time, SessionStore? store = null, TextWriter? log = null) : IAsyncDisposable
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private readonly Lock _gate = new();
    private readonly Dictionary<CacheKey, Flight> _flights = new();
    private bool _disposed;

    private sealed record CacheKey(string Agent, string Target, string Cwd);
    private sealed class Flight(string? revision, bool refresh)
    {
        public readonly string? Revision = revision;
        public readonly bool Refresh = refresh;
        public readonly CancellationTokenSource Stop = new();
        public bool Stopping;
        public Task<ComposerCatalogue?> Answer = null!;
        public int Waiters;
    }

    public async Task<ComposerCatalogue> DiscoverAsync(string agent, string targetId, string cwd, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(targetId)) throw new CallFailed("Select the agent configuration again.");
        launcher.ValidateFolder(cwd);
        var captured = ConfigTargetIdentity.Capture(launcher, agent, targetId);
        var key = new CacheKey(agent, targetId, cwd);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Flight flight;
            bool joining;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var revision = launcher.CatalogueRevision(captured.Descriptor, captured.Environment, cwd);
                var known = revision is null ? null : LoadSaved(key, revision, ct);
                if (!_flights.TryGetValue(key, out flight!))
                {
                    flight = new Flight(revision, known is not null);
                    _flights.Add(key, flight);
                    flight.Answer = RunProbeAsync(key, flight, captured);
                }

                joining = !flight.Stopping && flight.Revision == revision && (!flight.Refresh || known is not null);
                if (joining)
                {
                    if (known is not null) return known;
                    flight.Waiters++;
                }
                else if (flight.Revision != revision)
                {
                    Stop(flight);
                }
            }

            if (!joining)
            {
                // A replaced or abandoned probe still owns its adapter until it has finished closing it.
                await ObserveAsync(flight.Answer).WaitAsync(ct);
                continue;
            }

            try
            {
                return (await flight.Answer.WaitAsync(ct))!;
            }
            finally
            {
                bool joinCleanup;
                lock (_gate)
                {
                    flight.Waiters--;
                    joinCleanup = flight.Waiters == 0 && !flight.Refresh && _flights.GetValueOrDefault(key) == flight;
                    if (joinCleanup) Stop(flight);
                }
                if (joinCleanup) await ObserveAsync(flight.Answer);
            }
        }
    }

    private async Task<ComposerCatalogue?> RunProbeAsync(CacheKey key, Flight flight,
        (AgentDescriptor Descriptor, IReadOnlyDictionary<string, string> Environment, string TargetId) captured)
    {
        // Register the task before starting the adapter, including when the launcher completes synchronously.
        await Task.Yield();
        var ct = flight.Stop.Token;
        try
        {
            ct.ThrowIfCancellationRequested();
            var result = await ProbeAsync(captured, key.Cwd, ct);
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                if (flight.Revision is { } revision)
                {
                    var current = launcher.CatalogueRevision(captured.Descriptor, captured.Environment, key.Cwd);
                    WriteSaved(key, revision, revision == current ? result : null);
                }
                return result;
            }
        }
        catch (Exception error) when (flight.Refresh && error is not OutOfMemoryException)
        {
            lock (_gate)
            {
                if (!flight.Stopping && flight.Revision is { } revision)
                    WriteSaved(key, revision, null);
            }
            log?.WriteLine($"composer settings refresh for {key.Agent} failed: {error.GetType().Name}");
            return null;
        }
        finally
        {
            lock (_gate)
            {
                _flights.Remove(key);
                flight.Stop.Dispose();
            }
        }
    }

    private void WriteSaved(CacheKey key, string revision, ComposerCatalogue? catalogue)
    {
        try
        {
            store?.SaveCatalogue(key.Agent, key.Target, key.Cwd, revision,
                catalogue is null ? null : ComposerConfigJson.Json(catalogue.Config, catalogue.ModelOptions).GetRawText());
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log?.WriteLine($"composer settings for {key.Agent} could not be saved: {error.GetType().Name}");
        }
    }

    private ComposerCatalogue? LoadSaved(CacheKey key, string revision, CancellationToken ct)
    {
        if (store?.SavedCatalogue(key.Agent, key.Target, key.Cwd, revision, ct) is not { } saved) return null;
        try { return ComposerConfigJson.ParseCatalogue(saved); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }

    public void Invalidate(string agent)
    {
        lock (_gate)
        {
            foreach (var (key, flight) in _flights)
                if (key.Agent == agent) Stop(flight);
            store?.ClearCatalogues(agent);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] probes;
        lock (_gate)
        {
            _disposed = true;
            probes = _flights.Values.Select(flight =>
            {
                Stop(flight);
                return flight.Answer;
            }).ToArray();
        }
        await Task.WhenAll(probes.Select(ObserveAsync));
    }

    private static void Stop(Flight flight)
    {
        if (flight.Stopping) return;
        flight.Stopping = true;
        flight.Stop.Cancel();
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A waiter receives the outcome. A replacement or shutdown only joins cleanup.
        }
    }

    private async Task<ComposerCatalogue> ProbeAsync(
        (AgentDescriptor Descriptor, IReadOnlyDictionary<string, string> Environment, string TargetId) captured,
        string cwd, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(Limit, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var token = linked.Token;
        IAgentLink? link = null;
        UpdateRouter? updates = null;
        try
        {
            link = await launcher.LaunchConfiguredAsync(captured.Descriptor, captured.Environment, new StageTimes(time), token);
            token.ThrowIfCancellationRequested();
            await link.Client.InitializeAsync(new Implementation("assembly-backend", "0.1.0"), launcher.CanRunTerminalAuthentication, token);
            updates = new UpdateRouter(link.Client);
            var opened = await link.Client.NewSessionAsync(new NewSessionRequest(cwd, []), token);
            var initial = ConfigObservation.Read(updates, opened.SessionId, opened.ConfigOptions,
                opened.AvailableCommands.ValueKind == JsonValueKind.Array ? opened.AvailableCommands.GetRawText() : null);
            var models = new Dictionary<string, string>();
            var model = ConfigOptions.Model(initial.Options);
            if (model is { } selector)
            {
                if (selector.Current is { } current) models[current] = initial.Options.GetRawText();
                foreach (var value in selector.Values.Where(value => value != selector.Current))
                {
                    try
                    {
                        var result = await link.Client.SetConfigOptionAsync(opened.SessionId, selector.Id, value, token);
                        var observed = ConfigObservation.Read(updates, opened.SessionId, result.ConfigOptions);
                        if (ConfigOptions.ModelValue(observed.Options) == value) models[value] = observed.Options.GetRawText();
                    }
                    catch (JsonRpcException error) when (!error.Error.IsAuthRequired)
                    { /* A refused model is unavailable; never invent its effort. */ }
                }
            }
            token.ThrowIfCancellationRequested();
            ConfigTargetIdentity.Validate(launcher, captured.Descriptor, captured.Environment);
            return new ComposerCatalogue(new SessionConfig(initial.RawOptions, new Dictionary<string, string>(), initial.Commands), models);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        { throw new CallFailed("Provider settings did not answer within 15 seconds."); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CallFailed) { throw; }
        catch (JsonRpcException error) when (error.Error.IsAuthRequired)
        { throw new CallFailed("Sign in to this provider to read its settings.", "authentication_required"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { throw new CallFailed("Provider settings are unavailable.", "settings_unavailable"); }
        finally
        {
            // Dispose the exact link before awaiting the router. No session/close or prompt is sent.
            if (link is not null) await link.DisposeAsync();
            if (updates is not null) await updates.Completion;
        }
    }
}

/// <summary>Ordered metadata only; replay/text/tool updates stay in the same router for their actual owner.</summary>
internal sealed record ConfigObservation(JsonElement Options, string? Commands)
{
    internal string? RawOptions => Options.ValueKind == JsonValueKind.Array ? Options.GetRawText() : null;
    internal static ConfigObservation Read(UpdateRouter router, string nativeId, JsonElement options, string? commands = null) =>
        router.Metadata(nativeId, options, commands);
}
