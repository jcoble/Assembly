using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Infrastructure.Remote.Install;

internal sealed class RemoteInstallFeature(RemoteInstallJobs jobs) : IFeature
{
    internal const string Name = "remoteInstall";
    public void Register(ICallRegistry calls)
    {
        calls.Topic(Name, RemoteInstallJson.Default.RemoteInstallJob, job => job.OperationId.ToString("D"), (key, call) =>
        {
            Owner(call);
            return key is not null && Guid.TryParse(key, out var id) && jobs.TryStatus(id) is { } job ? [job] : [];
        });
        calls.Read(Name, "inspect", RemoteInstallJson.Default.RemoteInstallInspect, RemoteInstallJson.Default.RemoteInstallBundle, async (args, call) =>
        { Owner(call); return (await RemoteInstallPackage.InspectAsync(args.BundleFile, call.Cancellation)).Bundle; });
        calls.Read(Name, "status", RemoteInstallJson.Default.RemoteInstallOperation, RemoteInstallJson.Default.RemoteInstallJob, (args, call) =>
        { Owner(call); return Task.FromResult(jobs.Status(args.OperationId)); });
        calls.Write(Name, "start", RemoteInstallJson.Default.RemoteInstallStart, RemoteInstallJson.Default.RemoteInstallJob, async (args, call) =>
        {
            Owner(call);
            if (jobs.Existing(args) is not null) return scope => jobs.Status(args.OperationId);
            var binding = await jobs.Capture(args, call.Cancellation);
            return scope =>
            {
                call.Cancellation.ThrowIfCancellationRequested();
                binding.RequireAdmission(scope);
                return jobs.Admit(args, binding, scope);
            };
        }, args => "remote-install:" + args.Machine);
        calls.Write(Name, "cancel", RemoteInstallJson.Default.RemoteInstallOperation, RemoteInstallJson.Default.RemoteInstallJob, (args, call) =>
        {
            Owner(call);
            return Task.FromResult<Func<IWriteScope, RemoteInstallJob>>(scope =>
            {
                call.Cancellation.ThrowIfCancellationRequested();
                var status = jobs.Status(args.OperationId);
                scope.AfterCommit(() => jobs.Cancel(args.OperationId));
                return status;
            });
        });
    }

    private static void Owner(CallContext call)
    { if (call.Peer is not OwnerPeer) throw new CallFailed("Owner access is required.", "forbidden"); }
}
