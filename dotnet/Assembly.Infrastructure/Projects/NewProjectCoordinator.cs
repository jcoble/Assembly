using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Projects;
using Assembly.Infrastructure.Git;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Projects;

/// <summary>The desktop owns registration; filesystem work remains on its captured execution owner.</summary>
internal sealed class NewProjectCoordinator(ProjectStore store, IAppDatabase db, IProjectFolders local,
    GitWriter writer, CheckoutWrites writes, GitOperations operations, RemoteConnections connections)
{
    internal async Task<NewProjectResult> CreateAsync(NewProjectCreate request, Guid operationId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Machine)) throw new CallBadArguments("Choose a machine.");
        var folder = new ProjectFolderCreate(request.ParentPath, request.Name, request.Kind, request.Source);
        if (request.Machine == "local")
        {
            var prepared = await new ProjectFolderCreation(local, writer, writes, operations).CreateAsync(folder, operationId, ct);
            var inspected = await local.InspectAsync(prepared.RootPath, ct);
            RequireInspection(prepared, inspected);
            operations.RequireDirectoryClaim(operationId);
            return Register(request.Machine, inspected, null);
        }
        var owner = await connections.CaptureProjectOwnerAsync(request.Machine, ct);
        await using var remote = new RemoteProjectPreparation(connections, owner, operations);
        operations.RetainRemotePreparation(operationId, remote);
        var created = await operations.PrepareRemoteProjectFolderAsync(folder, operationId, ct);
        var current = await remote.InspectFinalAsync(created, ct);
        RequireInspection(created, current);
        connections.RequireProjectOwner(owner, ct);
        return Register(request.Machine, current, owner);
    }

    private NewProjectResult Register(string machine, FolderInspection inspection, RemoteConnections.ProjectOwner? owner)
    {
        NewProjectResult? attempted = null;
        try
        {
            return db.Writer.Write("projects.create", scope =>
            {
                if (owner is not null) connections.RequireProjectOwner(scope, owner);
                var result = store.AddOrRestore(scope, machine, inspection.RootPath, inspection.Title, inspection.RepoKey);
                return attempted = new NewProjectResult(result.Project, inspection, result.AlreadyExisted);
            });
        }
        catch (Exception error)
        {
            ProjectRecord? actual;
            try { actual = attempted is null ? store.AtRoot(machine, inspection.RootPath) : store.Get(attempted.Project.Id); }
            catch { throw new CallUncertain(machine); }
            if (attempted is not null && actual is { RemovedAt: null } && actual == attempted.Project) return attempted;
            if (actual is not null && (attempted is not null || actual.RemovedAt is null)) throw new CallUncertain(machine);
            // Only proven absence of a live registration says not added. The prepared folder remains.
            throw new CallFailed("The folder was created at " + inspection.RootPath + " but was not added to Projects. Inspect it and use Add folder. " +
                (error is CallFailed ? error.Message : "The registry transaction did not complete."));
        }
    }
    private static void RequireInspection(FolderInspection prepared, FolderInspection current)
    {
        if (prepared.RootPath != current.RootPath || prepared.IsGit != current.IsGit || prepared.CommonDir != current.CommonDir || prepared.RepoKey != current.RepoKey)
            throw new CallFailed("The project folder changed. No project was added; inspect it before using Add folder.");
    }
}
