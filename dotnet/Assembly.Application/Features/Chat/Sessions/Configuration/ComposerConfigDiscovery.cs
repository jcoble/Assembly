using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions.Configuration;

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
