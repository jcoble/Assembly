using Assembly.Application.Common.Projects;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Projects;
using Assembly.Infrastructure.Git;

namespace Assembly.Infrastructure.Projects;

/// <summary>The owner-local New Project producer. Uses the existing inspector, writer and host lease.</summary>
internal sealed class ProjectFolderCreation(IProjectFolders folders, GitWriter writer, CheckoutWrites writes, GitOperations operations)
{
    internal async Task<FolderInspection> CreateAsync(ProjectFolderCreate request, Guid operationId, CancellationToken ct)
    {
        Validate(request);
        ct.ThrowIfCancellationRequested();
        var parent = await folders.InspectAsync(request.ParentPath, ct);
        RequireParent(parent);
        var destination = Path.Combine(parent.RootPath, request.Name);
        return await writes.RunAsync(parent.RootPath, () => writes.RunAsync(destination, async () =>
        {
            ct.ThrowIfCancellationRequested();
            var current = await folders.InspectAsync(parent.RootPath, ct);
            RequireParent(current);
            if (current.RootPath != parent.RootPath) throw new CallFailed("The parent folder changed. Inspect it again before creating a project.");
            operations.FolderMutationDispatched(operationId);
            try
            {
                var claim = ProjectDirectoryClaim.Reserve(parent.RootPath, request.Name);
                try { operations.RetainDirectoryClaim(operationId, claim); }
                catch { claim.Dispose(); throw; }
                claim.RequireCurrent();
                var finishing = ct;
                if (request.Kind == "git")
                {
                    finishing = operations.InitializationDispatched(operationId);
                    await writer.InitAsync(destination, finishing);
                }
                else if (request.Kind == "clone") await writer.CloneAsync(request.Source!, destination, ct);
                claim.RequireCurrent();
                var inspection = await folders.InspectAsync(destination, finishing);
                claim.RequireCurrent();
                if (inspection.RootPath != destination || inspection.IsGit != (request.Kind != "empty"))
                    throw new CallFailed("The created folder does not match the requested project kind.");
                return inspection;
            }
            catch (CallFailed error)
            { throw new CallFailed(error.Message + "\n\nNo project was added. A folder may remain at " + destination + ". Inspect it before using Add folder."); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new CallFailed("The project folder could not be confirmed. No project was added. A folder may remain at " + destination + ". Inspect it before using Add folder."); }
        }));
    }

    private static void Validate(ProjectFolderCreate request)
    {
        if (string.IsNullOrEmpty(request.Name) || request.Name is "." or ".." || request.Name.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new CallBadArguments("Choose one folder name, without path separators.");
        if (request.Kind is not ("empty" or "git" or "clone")) throw new CallBadArguments("Choose empty, git or clone.");
        if (request.Kind == "clone" ? string.IsNullOrWhiteSpace(request.Source) || request.Source.Contains('\0') : request.Source is not null)
            throw new CallBadArguments("A repository URL or path is required only for clone.");
    }
    private static void RequireParent(FolderInspection inspection)
    {
        if (inspection.IsGit) throw new CallFailed("Choose a parent outside an existing repository.");
    }
}
