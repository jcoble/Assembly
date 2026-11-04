using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Infrastructure.Remote.Install;

/// <summary>The production install: <c>latest</c> reads the signed release, <c>start</c> installs it on a saved machine.</summary>
internal sealed class RemoteReleaseFeature(RemoteReleaseFeed feed, RemoteReleaseJobs jobs) : IFeature
{
    internal const string Name = "remoteRelease";

    public void Register(ICallRegistry calls)
    {
        calls.Topic(Name, RemoteReleaseJson.Default.RemoteReleaseJob, job => job.OperationId.ToString("D"), (key, call) =>
        {
            Owner(call);
            return key is not null && Guid.TryParse(key, out var id) && jobs.TryStatus(id) is { } job ? [job] : [];
        });
        calls.Read(Name, "latest", RemoteReleaseJson.Default.RemoteReleaseLatestArgs, RemoteReleaseJson.Default.RemoteReleaseLatest, async (_, call) =>
        {
            Owner(call);
            var release = await feed.LatestAsync(call.Cancellation);
            return new RemoteReleaseLatest(release.Record.Version, release.Record.ProtocolVersion);
        });
        calls.Write(Name, "start", RemoteReleaseJson.Default.RemoteReleaseStart, RemoteReleaseJson.Default.RemoteReleaseJob, (args, call) =>
        {
            Owner(call);
            return Task.FromResult<Func<IWriteScope, RemoteReleaseJob>>(scope => jobs.Admit(args, scope));
        }, args => "remote-install:" + args.Machine);
        calls.Write(Name, "cancel", RemoteReleaseJson.Default.RemoteReleaseOperation, RemoteReleaseJson.Default.RemoteReleaseOperation, (args, call) =>
        {
            Owner(call);
            return Task.FromResult<Func<IWriteScope, RemoteReleaseOperation>>(scope =>
            {
                scope.AfterCommit(() => jobs.Cancel(args.OperationId));
                return args;
            });
        });
    }

    private static void Owner(CallContext call)
    { if (call.Peer is not OwnerPeer) throw new CallFailed("Owner access is required.", "forbidden"); }
}
