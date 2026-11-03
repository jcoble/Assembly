using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Transcript;

namespace Assembly.Infrastructure.Agents;

public sealed partial class AdapterLauncher
{
    public bool CanRunTerminalAuthentication => true;

    public Task<IAgentLink> LaunchConfiguredAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> effectiveEnvironment,
        StageTimes stages, CancellationToken cancellationToken) =>
        StartConfiguredAsync(descriptor, effectiveEnvironment, permissions, stages, cancellationToken);

    public Task<IAgentLink> LaunchSignInAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> effectiveEnvironment,
        IPermissionPolicy policy, StageTimes stages, CancellationToken cancellationToken) =>
        StartConfiguredAsync(descriptor, effectiveEnvironment, policy, stages, cancellationToken);

    private Task<IAgentLink> StartConfiguredAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> effectiveEnvironment,
        IPermissionPolicy policy, StageTimes stages, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ConfigTargetIdentity.Validate(this, descriptor, effectiveEnvironment);
        // The complete environment was captured by the caller. Do not read ambient state a second time.
        var environment = effectiveEnvironment.ToDictionary(pair => pair.Key, pair => pair.Value, ConfigTargetIdentity.EnvironmentComparer);
        if (descriptor.Cli is { Length: > 0 } cli)
        {
            var variable = AccountContext.ExecutableVariable(descriptor.VendorFamily);
            if (variable is not null) environment[variable] = cli;
        }
        Directory.CreateDirectory(checkouts);
        var link = AgentConnection.Start(new(descriptor.Command, descriptor.Args.ToArray(), checkouts,
            environment,
            ReplaceEnvironment: true), policy, TranscriptLog.Discard());
        stages.Mark("adapter");
        return Task.FromResult<IAgentLink>(link);
    }
}
