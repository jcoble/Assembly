using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Workspaces;

internal sealed class WorkspacesFeature(WorkspaceStore store) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        calls.Topic("workspaces", WorkspacesJson.Default.WorkspaceChange, change => change.Key, (key, call) =>
        {
            RequireOwner(call);
            if (key is null) return [];
            var parts = key.Split('/', 3);
            if (parts.Length != 3) throw new CallBadArguments("A workspace subscription needs its exact source/session/checkout key.");
            var change = store.Change(new WorkspaceKey(parts[0], parts[1], parts[2]), call.Cancellation);
            return change is null ? [] : [change];
        });
        calls.Read("workspaces", "read", WorkspacesJson.Default.WorkspaceReadArgs, WorkspacesJson.Default.WorkspaceReadResult,
            (args, call) =>
            {
                RequireOwner(call);
                return Task.FromResult(store.Read(new(args.Machine, args.SessionId, args.CheckoutRoot), call.Cancellation));
            }, bulk: true);
        calls.Write("workspaces", "save", WorkspacesJson.Default.WorkspaceSaveArgs, WorkspacesJson.Default.WorkspaceSaveResult, (args, call) =>
        {
            RequireOwner(call);
            call.Cancellation.ThrowIfCancellationRequested();
            return Task.FromResult<Func<IWriteScope, WorkspaceSaveResult>>(scope => store.Save(scope,
                new(args.Machine, args.SessionId, args.CheckoutRoot), args.ExpectedRevision, args.Snapshot));
        }, bulk: true);
        calls.Write("workspaces", "save_many", WorkspacesJson.Default.WorkspaceSaveManyArgs, WorkspacesJson.Default.WorkspaceSaveManyResult, (args, call) =>
        {
            RequireOwner(call);
            call.Cancellation.ThrowIfCancellationRequested();
            if (args?.Saves is not { Count: > 0 }) throw new CallBadArguments("A workspace batch needs at least one save.");
            var saves = args.Saves.ToArray();
            var keys = new HashSet<WorkspaceKey>();
            foreach (var save in saves)
            {
                if (save is null) throw new CallBadArguments("A workspace batch cannot contain a null save.");
                var key = new WorkspaceKey(save.Machine, save.SessionId, save.CheckoutRoot);
                if (!keys.Add(key)) throw new CallBadArguments("A workspace batch cannot save the same key twice.");
                WorkspaceStore.ValidateSave(key, save.ExpectedRevision, save.Snapshot);
            }
            // The batch budget includes its wrapper and keys, using this exact generated context's default encoder.
            if (JsonSerializer.SerializeToUtf8Bytes(new WorkspaceSaveManyArgs(saves), WorkspacesJson.Default.WorkspaceSaveManyArgs).Length > WorkspaceStore.MaxSnapshotBytes)
                throw new CallFailed("This workspace batch is larger than 3 MiB, so it was not saved.");
            return Task.FromResult<Func<IWriteScope, WorkspaceSaveManyResult>>(scope =>
            {
                var saved = new List<WorkspaceSaved>(saves.Length);
                foreach (var save in saves)
                {
                    var result = store.Save(scope, new(save.Machine, save.SessionId, save.CheckoutRoot), save.ExpectedRevision, save.Snapshot);
                    saved.Add(new(save.Machine, save.SessionId, save.CheckoutRoot, result.Revision));
                }
                return new(saved);
            });
        }, bulk: true);
    }

    private static void RequireOwner(CallContext call)
    {
        if (call.Peer is not OwnerPeer) throw new CallFailed("Desktop workspaces are available only to the owner.", "forbidden");
    }
}
