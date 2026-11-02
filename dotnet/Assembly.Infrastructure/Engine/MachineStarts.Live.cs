using Assembly.Application.Common.Machines;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine;

internal sealed partial class MachineStarts
{
    private sealed record CapturedMirror(RemoteMachine Remote, EngineMirror Mirror, EngineMirror.LiveMirrorBinding Live);

    private sealed class CapturedLiveBatch
    {
        private readonly IReadOnlyList<CapturedMirror> _owners;
        internal IReadOnlyList<MachineStartMirrorGeneration> Generations { get; }
        private CapturedLiveBatch(IReadOnlyList<CapturedMirror> owners)
        {
            _owners = owners;
            Generations = owners.Select(owner => new MachineStartMirrorGeneration(owner.Live.Machine, owner.Live.Generation)).ToArray();
        }

        internal static async Task<CapturedLiveBatch> CaptureAsync(RemoteConnections connections, string source, CancellationToken ct)
        {
            List<CapturedMirror> owners = [];
            HashSet<string> machines = new(StringComparer.Ordinal);
            foreach (var (remote, mirror) in connections.CaptureMirrorOwners())
            {
                if (await mirror.CaptureLiveBindingAsync(ct) is not { } live) continue;
                if (live.Machine == source || !machines.Add(live.Machine)) throw Refused("forbidden");
                owners.Add(new(remote, mirror, live));
            }
            return new(owners);
        }

        internal CapturedMirror Target(string machine) => _owners.SingleOrDefault(owner => owner.Live.Machine == machine)
            ?? throw new MachineUnreachable(machine, "Remote engine machine is disconnected or not caught up");

        internal async Task RequireCurrentAsync(CancellationToken ct)
        {
            foreach (var owner in _owners)
                if (!await owner.Mirror.IsCurrentLiveBindingAsync(owner.Live, ct))
                    throw new MachineUnreachable(owner.Live.Machine, "Remote engine readiness changed");
        }
    }
}
