using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Projects;
using Assembly.Infrastructure.Git;

namespace Assembly.Infrastructure.Projects;

/// <summary>Only the reviewed pre-session project actions; the existing operation owns their continuation.</summary>
internal sealed class GitProjectOperations(GitOperations operations, ProjectFolderCreation folders,
    NewProjectCoordinator? projects = null) : IFeature
{
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();
    public void Register(ICallRegistry calls)
    {
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
                else
                {
                    var request = args.Args.Deserialize(ProjectsJson.Default.ProjectFolderCreate) ?? throw new CallBadArguments("Folder arguments are required.");
                    accepted = operations.Start(args, async ct => JsonSerializer.SerializeToElement(
                        await folders.CreateAsync(request, args.OperationId, ct), ProjectsJson.Default.FolderInspection));
                }
                return Task.FromResult<Func<IWriteScope, GitOperationStatus>>(_ => accepted);
            });
        calls.Write("git", "cancel", GitOperationsJson.Default.GitOperationArgs, GitOperationsJson.Default.JsonElement,
            async (args, call) =>
            {
                Admit(args.Action, args.Session, args.Machine, call); operations.RequireScope(args);
                await operations.CancelProjectAsync(args.OperationId, call.Cancellation);
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
                if (call.Peer is not OwnerPeer || key is null || !key.StartsWith(operations.Machine + "/", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(key[(operations.Machine.Length + 1)..], "D", out var id)) throw new CallFailed("forbidden");
                var status = operations.Observe(id, call.Connection);
                return status is null ? [] : [status];
            });
        calls.Closed(operations.Closed);
    }

    private void Admit(string? action, string? session, string? machine, CallContext call)
    {
        if (call.Peer is not OwnerPeer || session is not null || machine is not null ||
            action is not ("createProject" or "prepareProjectFolder") || action == "createProject" && projects is null)
            throw new CallFailed("forbidden");
    }
}
