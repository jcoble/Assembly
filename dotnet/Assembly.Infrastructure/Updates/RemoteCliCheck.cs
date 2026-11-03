using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Agents;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Agents;

namespace Assembly.Infrastructure.Updates;

/// <summary>Runs exclusively on the owning machine, including when its backend listener is disconnected.</summary>
internal sealed class RemoteCliCheck(TimeProvider time)
{
    internal async Task<RemoteCliReadResult> ReadAsync(RemoteCliCaptureData capture, CliCheckArgs request, CancellationToken ct)
    {
        if (request.MachineId != capture.MachineId || !AccountContext.IsValid(request.AccountContext))
            throw RemoteCliCapture.Unavailable();
        var target = capture.Targets.SingleOrDefault(t => t.Descriptor.Id == request.ProviderId &&
            t.ConfigurationTargetId == request.ConfigurationTargetId && t.AccountContext == request.AccountContext)
            ?? throw RemoteCliCapture.Unavailable();
        var launcher = new AgentsOnlyLauncher(() => RemoteCliCapture.ReadCurrentAgents(capture));
        Validate();
        CliInstalled? installed = null;
        string? reason = null;
        try { installed = await new CliCommand(time).ProbeAsync(launcher, target.Descriptor, target.Environment, ct); }
        catch (Assembly.Application.Common.Calls.CallFailed error) { reason = error.Reason ?? "probe_failed"; }
        Validate();
        if (installed is not null) await CliCommand.RequireUnchangedAsync(installed, ct);
        ct.ThrowIfCancellationRequested();
        return new(capture.MachineId, capture.InstallationId, request.ProviderId, request.AccountContext,
            request.ConfigurationTargetId, installed?.Version, installed?.Manager ?? "unknown", installed?.Source ?? "unknown", reason);

        void Validate()
        {
            ConfigTargetIdentity.Validate(launcher, target.Descriptor, target.Environment);
            CliCommand.RequireSelectedConfiguration(launcher, target.Descriptor, target.Environment);
            if (ConfigTargetIdentity.Of(target.Descriptor, target.Environment) != request.ConfigurationTargetId ||
                AccountContext.Of(target.Descriptor, k => target.Environment.GetValueOrDefault(k)) != request.AccountContext)
                throw RemoteCliCapture.Unavailable();
        }
    }
}

// A configuration reader, with every launch/workspace operation unavailable. The fixed CLI probe never calls them.
internal sealed class AgentsOnlyLauncher(Func<IReadOnlyList<AgentDescriptor>> read) : IAgentLauncher
{
    public IReadOnlyList<AgentDescriptor> Agents => read();
    public bool CanRunTerminalAuthentication => false;
    public Task<string> CheckoutAsync(SessionInfo session, StageTimes stages, CancellationToken cancellationToken) => throw new NotSupportedException();
    public void ValidateFolder(string folder) => throw new NotSupportedException();
    public Task<IAgentLink> LaunchAsync(string agentId, StageTimes stages, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IAgentLink> LaunchConfiguredAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> effectiveEnvironment,
        StageTimes stages, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IAgentLink> LaunchSignInAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> effectiveEnvironment,
        IPermissionPolicy policy, StageTimes stages, CancellationToken cancellationToken) => throw new NotSupportedException();
}
