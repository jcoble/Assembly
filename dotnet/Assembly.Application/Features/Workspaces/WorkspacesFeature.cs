using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Workspaces;

internal sealed class WorkspacesFeature(WorkspaceStore store) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        calls.Topic("workspaces", WorkspacesJson.Default.WorkspaceChange, change => change.Key, (key, call) =>
        {
            if (key is null) return [];
            var parts = key.Split('/', 3);
            if (parts.Length != 3) throw new CallBadArguments("A workspace subscription needs its exact source/session/checkout key.");
            var change = store.Change(new WorkspaceKey(parts[0], parts[1], parts[2]), call.Cancellation);
            return change is null ? [] : [change];
        });
        calls.Read("workspaces", "read", WorkspacesJson.Default.WorkspaceReadArgs, WorkspacesJson.Default.WorkspaceReadResult,
            (args, call) => Task.FromResult(store.Read(new(args.Machine, args.SessionId, args.CheckoutRoot), call.Cancellation)), bulk: true);
        calls.Write("workspaces", "save", WorkspacesJson.Default.WorkspaceSaveArgs, WorkspacesJson.Default.WorkspaceSaveResult, (args, call) =>
        {
            call.Cancellation.ThrowIfCancellationRequested();
            return Task.FromResult<Func<IWriteScope, WorkspaceSaveResult>>(scope => store.Save(scope,
                new(args.Machine, args.SessionId, args.CheckoutRoot), args.ExpectedRevision, args.Snapshot));
        }, bulk: true);
    }
}
