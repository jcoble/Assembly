using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Remote.Install;

/// <summary>The signed release: <c>latest</c> reads it. Installing goes through the update flow, one job per machine.</summary>
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
    }

    private static void Owner(CallContext call)
    { if (call.Peer is not OwnerPeer) throw new CallFailed("Owner access is required.", "forbidden"); }
}
