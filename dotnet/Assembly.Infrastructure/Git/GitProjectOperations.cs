using Assembly.Application.Common.Projects;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Projects;
using Assembly.Infrastructure.Projects;
using Assembly.Infrastructure.Chat.SourceControl;

namespace Assembly.Infrastructure.Git;

/// <summary>The fixed project and session producers share one host-owned operation lifetime.</summary>
internal sealed class GitProjectOperations(GitOperations operations, ProjectFolderCreation folders,
    GitSessionOperations sessionOperations, SourceControlFeature routes, NewProjectCoordinator? projects = null) : IFeature
{
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();
    public void Register(ICallRegistry calls)
    {
        calls = routes.Route(calls);
        calls.Read("git", "operation_host", GitOperationsJson.Default.GitOperationHostArgs, GitOperationsJson.Default.GitOperationHost,
            (args, call) =>
            {
                Admit(args.Action, args.Session, args.Machine, call);
                return Task.FromResult(new GitOperationHost(operations.InstanceId, operations.Machine));
            });
        calls.Read("git", "operation", GitOperationsJson.Default.GitOperationArgs, GitOperationsJson.Default.GitOperationStatus,
            (args, call) =>
            {
                Admit(args.Action, args.Session, args.Machine, call); operations.RequireScope(args);
                return Task.FromResult(operations.Status(args.OperationId));
            });
        calls.Write("git", "start", GitOperationsJson.Default.GitOperationStart, GitOperationsJson.Default.GitOperationStatus,
            (args, call) =>
            {
                Admit(args.Action, args.Session, args.Machine, call);
                GitOperationStatus accepted;
                if (args.Action == "createProject")
                {
                    var request = args.Args.Deserialize(ProjectsJson.Default.NewProjectCreate) ?? throw new CallBadArguments("Project arguments are required.");
                    accepted = operations.Start(args, async ct => JsonSerializer.SerializeToElement(
                        await projects!.CreateAsync(request, args.OperationId, ct), ProjectsJson.Default.NewProjectResult));
                }
                else if (args.Action == "prepareProjectFolder")
                {
                    var request = args.Args.Deserialize(ProjectsJson.Default.ProjectFolderCreate) ?? throw new CallBadArguments("Folder arguments are required.");
                    accepted = operations.Start(args, async ct => JsonSerializer.SerializeToElement(
                        await folders.CreateAsync(request, args.OperationId, ct), ProjectsJson.Default.FolderInspection));
                }
                else accepted = operations.Start(args, sessionOperations.Prepare(args));
                return Task.FromResult<Func<IWriteScope, GitOperationStatus>>(_ => accepted);
            });
        calls.Write("git", "cancel", GitOperationsJson.Default.GitOperationArgs, GitOperationsJson.Default.JsonElement,
            async (args, call) =>
            {
                Admit(args.Action, args.Session, args.Machine, call); operations.RequireScope(args);
                if (args.Action is "createProject" or "prepareProjectFolder")
                    await operations.CancelProjectAsync(args.OperationId, call.Cancellation);
                else operations.Cancel(args.OperationId);
                return _ => Empty;
            });
        calls.Write("git", "release", GitOperationsJson.Default.GitOperationArgs, GitOperationsJson.Default.JsonElement,
            (args, call) =>
            {
                Admit(args.Action, args.Session, args.Machine, call); operations.RequireScope(args);
                operations.Release(args.OperationId);
                return Task.FromResult<Func<IWriteScope, JsonElement>>(_ => Empty);
            });
        calls.Topic("gitOperation", GitOperationsJson.Default.GitOperationStatus, status => operations.Key(status.OperationId),
            (key, call) =>
            {
                if (key is not null && !key.StartsWith(operations.Machine + "/", StringComparison.Ordinal))
                    return routes.ObserveRemoteOperation(key, call, operations);
                if (call.Peer is not OwnerPeer || key is null || !key.StartsWith(operations.Machine + "/", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(key[(operations.Machine.Length + 1)..], "D", out var id)) throw new CallFailed("forbidden");
                var status = operations.Observe(id, call.Connection);
                return status is null ? [] : [status];
            });
        calls.Closed(operations.Closed);
    }

    private void Admit(string? action, string? session, string? machine, CallContext call)
    {
        if (call.Peer is not OwnerPeer || machine is not null) throw new CallFailed("forbidden");
        if (action is "createProject" or "prepareProjectFolder")
        {
            if (session is not null || action == "createProject" && projects is null) throw new CallFailed("forbidden");
            return;
        }
        if (!GitSessionOperations.Supports(action) || string.IsNullOrWhiteSpace(session)) throw new CallFailed("forbidden");
        sessionOperations.RequireSession(session);
    }
}
