using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;

namespace Assembly.Infrastructure.Remote.Install;

internal sealed record RemoteInstallBinding(SavedMachine Profile, string Engine, Action<CancellationToken> RequireCurrent,
    Action<CancellationToken> RequireProfile, Action<IWriteScope> RequireAdmission);

// An explicit temporary target captured for this task, not a home/path convention for arbitrary machines.
internal static class RemoteInstallTarget
{
    internal const string Root = "/home/blackcolours/assembly-dotnet-tsk-1649-engine-integration-fresh";
    internal const string Engine = "872c2b61014142e2aea983c56e1f1047";
    internal const string Unit = "assembly-dotnet-tsk-1649-engine-integration-fresh.service";
    internal static void Require(RemoteInstallBinding binding, CancellationToken ct)
    {
        if (binding.Profile.Ssh != "workbox-test" || binding.Profile.Port != 9887 ||
            binding.Profile.TokenFile != Root + "/serve/env" || binding.Engine != Engine)
            throw new CallFailed("Only the captured Workbox Test lane is supported.", "unsupported_target");
        binding.RequireCurrent(ct);
    }
}
