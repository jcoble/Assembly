using System.Text.Json;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>A private, bounded metadata probe. Never owns an Assembly session or emits rows.</summary>
public sealed class ComposerConfigDiscovery(IAgentLauncher launcher, TimeProvider time)
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    public async Task<ComposerCatalogue> DiscoverAsync(string agent, string targetId, string cwd, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(targetId)) throw new CallFailed("Select the agent configuration again.");
        launcher.ValidateFolder(cwd);
        var captured = ConfigTargetIdentity.Capture(launcher, agent, targetId);
        using var deadline = new CancellationTokenSource(Limit, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var caller = ct;
        ct = linked.Token;
        var link = await launcher.LaunchConfiguredAsync(captured.Descriptor, captured.Environment, new StageTimes(time), ct);
        UpdateRouter? updates = null;
        try
        {
            await link.Client.InitializeAsync(new Implementation("assembly-backend", "0.1.0"), captured.Descriptor.Air, ct);
            updates = new UpdateRouter(link.Client.Updates);
            var opened = await link.Client.NewSessionAsync(cwd, ct);
            var initial = ConfigObservation.Read(updates, opened.SessionId, opened.ConfigOptions);
            var models = new Dictionary<string, string>();
            var model = ConfigOptions.Model(initial.Options);
            if (model is { } selector)
            {
                if (selector.Current is { } current) models[current] = initial.Options.GetRawText();
                foreach (var value in selector.Values.Where(value => value != selector.Current))
                {
                    try
                    {
                        var result = await link.Client.SetConfigOptionAsync(opened.SessionId, selector.Id, value, ct);
                        var observed = ConfigObservation.Read(updates, opened.SessionId, result.ConfigOptions);
                        if (ConfigOptions.Model(observed.Options)?.Current == value) models[value] = observed.Options.GetRawText();
                    }
                    catch (JsonRpcException) { /* A refused model is unavailable; never invent its effort. */ }
                }
            }
            ct.ThrowIfCancellationRequested();
            ConfigTargetIdentity.Capture(launcher, agent, targetId);
            return new ComposerCatalogue(new SessionConfig(initial.RawOptions, new Dictionary<string, string>(), initial.Commands), models);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !caller.IsCancellationRequested)
        {
            throw new CallFailed("Provider settings did not answer within 15 seconds.");
        }
        catch (JsonRpcException error) { throw new CallFailed($"Settings unavailable: {error.Message}"); }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        { throw new CallFailed("The agent ended before its settings were available."); }
        finally
        {
            // Dispose the exact link before awaiting the router. No session/close or prompt is sent.
            await link.DisposeAsync();
            if (updates is not null) await updates.Completion;
        }
    }
}

/// <summary>Only metadata notifications preceding the ACP response are observed; private updates never reach a converter.</summary>
internal sealed record ConfigObservation(JsonElement Options, string? Commands)
{
    internal string? RawOptions => Options.ValueKind == JsonValueKind.Array ? Options.GetRawText() : null;

    internal static ConfigObservation Read(UpdateRouter router, string nativeId, JsonElement options, string? commands = null)
    {
        return router.Metadata(nativeId, options, commands);
    }
}
