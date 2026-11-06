using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;

namespace Assembly.Infrastructure.Updates;

/// <summary>Only the owner-selected CLI component check on its exact machine; no coordinator or update actions.</summary>
internal sealed class UpdateCalls(IAgentLauncher launcher, string machine, HttpClient http, TimeProvider time,
    CancellationToken hostStop, Func<string, CancellationToken, RemoteCliProfile?> remoteLocator,
    RemoteCliClient remoteClient) : IFeature
{
    public void Register(ICallRegistry calls) => calls.Read("updates", "checkCli", UpdatesJson.Default.CliCheckArgs,
        UpdatesJson.Default.CliCheckResult, CheckAsync);

    internal async Task<CliCheckResult> CheckAsync(CliCheckArgs args, CallContext call)
    {
        if (call.Peer is not OwnerPeer) throw Refuse("forbidden");
        if (string.IsNullOrEmpty(args.MachineId)) throw Refuse("wrong_machine");
        if (string.IsNullOrEmpty(args.ProviderId) || !AccountContext.IsValid(args.AccountContext) ||
            args.ConfigurationTargetId is null || args.ConfigurationTargetId.Length != 64 ||
            args.ConfigurationTargetId.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Refuse("invalid_selection");
        if (args.MachineId != machine)
        {
            using var remoteStop = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, call.Closed, hostStop);
            remoteStop.Token.ThrowIfCancellationRequested();
            var binding = RemoteBinding(args, remoteStop.Token) ?? throw Refuse("configuration_unavailable");
            return await remoteClient.CheckAsync(binding, args, () => RemoteBinding(args, remoteStop.Token), remoteStop.Token, hostStop);
        }
        var captured = ConfigTargetIdentity.Capture(launcher, args.ProviderId, args.ConfigurationTargetId);
        CliCommand.RequireSelectedConfiguration(launcher, captured.Descriptor, captured.Environment);
        if (AccountContext.Of(captured.Descriptor, key => captured.Environment.GetValueOrDefault(key)) != args.AccountContext)
            throw Refuse("selection_changed");
        if (!CliCommand.Supports(captured.Descriptor.VendorFamily)) throw Refuse("unsupported_provider");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20), time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, call.Closed, hostStop, deadline.Token);
        CliInstalled? installed = null; CliCandidate? candidate = null; string? reason = null;
        var command = new CliCommand(time);
        try
        {
            try { installed = await command.ProbeAsync(launcher, captured.Descriptor, captured.Environment, stop.Token); }
            catch (CallFailed error) { reason = error.Reason ?? "probe_failed"; }
            try { candidate = await new CliReleaseSource(http, time).LatestAsync(captured.Descriptor.VendorFamily!, stop.Token); }
            catch (CallFailed) { reason ??= "release_unverified"; }
            CliCommand.RequireSelectedConfiguration(launcher, captured.Descriptor, captured.Environment);
            if (AccountContext.Of(captured.Descriptor, key => captured.Environment.GetValueOrDefault(key)) != args.AccountContext)
                throw Refuse("selection_changed");
            if (installed is not null) await CliCommand.RequireUnchangedAsync(installed, stop.Token);
            stop.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            call.Cancellation.ThrowIfCancellationRequested(); call.Closed.ThrowIfCancellationRequested(); hostStop.ThrowIfCancellationRequested();
            installed = null; candidate = null; reason = "timeout";
        }
        catch (CallFailed) { throw Refuse("selection_changed"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { throw Refuse("installation_changed"); }
        var comparison = reason is not null || installed is null || candidate is null ? "unverified"
            : installed.Source != "npm" || installed.Manager != "npm" ? "unsupported"
            : CliVersion.Compare(installed.Version, candidate.Version);
        if (comparison == "unsupported") reason = "unsupported_installation";
        return new(args.MachineId, args.ProviderId, args.AccountContext, args.ConfigurationTargetId,
            installed?.Version, installed?.Manager ?? "unknown", installed?.Source ?? "unknown", candidate, comparison, reason);
    }

    private RemoteCliBinding? RemoteBinding(CliCheckArgs args, CancellationToken cancel)
    {
        var saved = remoteLocator(args.MachineId, cancel);
        if (saved is null || saved.Locator.MachineId != args.MachineId) return null;
        var locator = saved.Locator;
        locator.Validate();
        var target = locator.Targets.SingleOrDefault(target => target.ProviderId == args.ProviderId &&
            target.AccountContext == args.AccountContext && target.ConfigurationTargetId == args.ConfigurationTargetId);
        if (target is null) return null;
        if (!CliCommand.Supports(target.VendorFamily)) throw Refuse("unsupported_provider");
        return new(saved.Profile.Profile, locator.MachineId, locator.InstallationId, locator.CaptureFile,
            new(locator.HelperExecutable, locator.HelperArguments.ToArray(), locator.HelperWorkingDirectory),
            target.VendorFamily, target.ProviderId, target.AccountContext, target.ConfigurationTargetId, saved.Profile.Generation);
    }

    private static CallFailed Refuse(string reason) => new("CLI check unavailable.", reason);
}
