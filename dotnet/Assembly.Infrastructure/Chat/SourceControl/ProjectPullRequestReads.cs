using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Projects;
using Assembly.Application.Features.Chat.SourceControl.PullRequests;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Chat.SourceControl;

/// <summary>Project-only PR reads retain the registry tuple and the original owning Engine source.</summary>
internal sealed class ProjectPullRequestReads(IAppDatabase db, IProjectRoots roots, SessionRowSources sources,
    string machine, PullRequestReader reader, MachineForward? forward, RemoteConnections? connections)
{
    internal Task<PullRequestDetail> DetailAsync(string projectId, ExpectedSource source, int number, CallContext call) =>
        Read(projectId, source, call, "project_pr", target => new OwnerDetail(target.Source.Machine, projectId, target.Root.RootPath, target.Repository, number,
            new(target.Source.Machine, 0, null)), ProjectPrJson.Default.OwnerDetail, ProjectPrJson.Default.PullRequestDetail,
            target => reader.ProjectDetailAsync(target.Root.RootPath, target.Repository, number, call.Cancellation));

    internal Task<PrFileContext> FileAsync(string projectId, ExpectedSource source, int number, string path, string baseSha, string headSha, CallContext call) =>
        Read(projectId, source, call, "project_prFile", target => new OwnerFile(target.Source.Machine, projectId, target.Root.RootPath, target.Repository, number,
            path, baseSha, headSha, new(target.Source.Machine, 0, null)), ProjectPrJson.Default.OwnerFile, ProjectPrJson.Default.PrFileContext,
            target => reader.ProjectFileAsync(target.Root.RootPath, target.Repository, number, path, baseSha, headSha, call.Cancellation));

    private async Task<TResult> Read<TArgs, TResult>(string id, ExpectedSource source, CallContext call, string method,
        Func<Target, TArgs> request, JsonTypeInfo<TArgs> input, JsonTypeInfo<TResult> output, Func<Target, Task<TResult>> local)
    {
        if (call.Peer is not OwnerPeer || call.ExpectedSource is not null && call.ExpectedSource != source) throw Changed();
        var sourceGuard = await sources.CaptureEngineAsync(source, call.Cancellation); // Before any registry lookup.
        var handedOff = false;
        try
        {
            ProjectRoot project;
            using (var scope = db.OpenSnapshot(call.Cancellation)) project = roots.Find(scope, id) ?? throw Changed();
            if (project.Removed || string.IsNullOrWhiteSpace(project.RootPath) || !Path.IsPathFullyQualified(project.RootPath)) throw Changed();
            if (project.RepoKey is not { } key || !key.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
                throw new CallFailed("This project has no GitHub repository.");
            var repository = GitHubRepository.Parse(key["github.com/".Length..]);
            RemoteConnections.ProjectOwner? owner = null;
            EngineMirror.LiveMirrorBinding? live = null;
            if (source.Machine == machine)
            {
                if (project.Machine != "local" || source.Generation != 0 || source.RouteMachine is not null) throw Changed();
            }
            else
            {
                if (connections is null || forward is null || source.RouteMachine != project.Machine) throw Changed();
                var captured = await connections.CaptureSessionOpenSourceAsync(db, source, call.Cancellation);
                owner = await connections.CaptureProjectOwnerAsync(project.Machine, call.Cancellation);
                if (owner.Engine != source.Machine || !ReferenceEquals(owner.Machine, captured.Machine) ||
                    !ReferenceEquals(owner.Binding, captured.Live.OwnerBinding)) throw Changed();
                live = captured.Live;
            }
            void Ensure()
            {
                sourceGuard.EnsureCurrent();
                using var scope = db.OpenSnapshot(CancellationToken.None);
                if (roots.Find(scope, id) != project) throw Changed();
                if (owner is not null) connections!.RequireProjectOwner(owner, CancellationToken.None);
            }
            call.SourceGuard = new ProjectGuard(sourceGuard, Ensure);
            handedOff = true;
            Ensure();
            var target = new Target(project, repository, source);
            TResult result;
            if (owner is null) result = await local(target);
            else
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, sourceGuard.Changed);
                var answer = await forward!.CallAsync(owner.Engine, "git", method, JsonSerializer.SerializeToElement(request(target), input), true,
                    stop.Token, owner.Socket, owner.Binding, live);
                result = answer.Deserialize(output) ?? throw Changed();
            }
            Ensure();
            return result;
        }
        catch { if (!handedOff) sourceGuard.Dispose(); throw; }
    }

    internal async Task<PullRequestDetail> OwnerDetailAsync(OwnerDetail args, CallContext call)
    {
        await RequireOwner(args.TargetMachine, args.ProjectId, args.Root, args.Repository, args.ExpectedSource, call);
        return await reader.ProjectDetailAsync(args.Root, args.Repository, args.Number, call.Cancellation);
    }
    internal async Task<PrFileContext> OwnerFileAsync(OwnerFile args, CallContext call)
    {
        await RequireOwner(args.TargetMachine, args.ProjectId, args.Root, args.Repository, args.ExpectedSource, call);
        return await reader.ProjectFileAsync(args.Root, args.Repository, args.Number, args.Path, args.BaseSha, args.HeadSha, call.Cancellation);
    }
    private async Task RequireOwner(string destination, string projectId, string root, string repository, ExpectedSource source, CallContext call)
    {
        if (call.Peer is not OwnerPeer || destination != machine || source != new ExpectedSource(machine, 0, null) ||
            string.IsNullOrWhiteSpace(projectId) || !Path.IsPathFullyQualified(root)) throw Changed();
        GitHubRepository.Parse(repository);
        call.SourceGuard = await sources.CaptureEngineAsync(source, call.Cancellation);
        call.SourceGuard.EnsureCurrent();
    }
    private sealed record Target(ProjectRoot Root, string Repository, ExpectedSource Source);
    private sealed class ProjectGuard(IExpectedSourceGuard source, Action ensure) : IExpectedSourceGuard
    {
        public CancellationToken Changed => source.Changed;
        public void EnsureCurrent() => ensure();
        public void Dispose() => source.Dispose();
    }
    private static CallFailed Changed() => new("The selected project or its owning source changed. Refresh and try again.", "source_changed");
    internal sealed record OwnerDetail([property: JsonRequired] string TargetMachine, [property: JsonRequired] string ProjectId,
        [property: JsonRequired] string Root, [property: JsonRequired] string Repository, int Number, [property: JsonRequired] ExpectedSource ExpectedSource);
    internal sealed record OwnerFile([property: JsonRequired] string TargetMachine, [property: JsonRequired] string ProjectId,
        [property: JsonRequired] string Root, [property: JsonRequired] string Repository, int Number, [property: JsonRequired] string Path,
        [property: JsonRequired] string BaseSha, [property: JsonRequired] string HeadSha, [property: JsonRequired] ExpectedSource ExpectedSource);
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProjectPullRequestReads.OwnerDetail))]
[JsonSerializable(typeof(ProjectPullRequestReads.OwnerFile))]
[JsonSerializable(typeof(PullRequestDetail))]
[JsonSerializable(typeof(PrFileContext))]
internal sealed partial class ProjectPrJson : JsonSerializerContext;
