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
        using var target = await CaptureAsync(id, source, call);
        call.SourceGuard = target.Retain();
        TResult result;
        if (target.Owner is null) result = await local(target);
        else
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, target.Changed);
            var answer = await forward!.CallAsync(target.Owner.Engine, "git", method, JsonSerializer.SerializeToElement(request(target), input), true,
                stop.Token, target.Owner.Socket, target.Owner.Binding, target.Live);
            result = answer.Deserialize(output) ?? throw Changed();
        }
        target.EnsureCurrent();
        return result;
    }

    internal async Task<Target> CaptureAsync(string id, ExpectedSource source, CallContext call)
    {
        if (call.Peer is not OwnerPeer || call.ExpectedSource is not null && call.ExpectedSource != source) throw Changed();
        var sourceGuard = await sources.CaptureEngineAsync(source, call.Cancellation); // Before any registry lookup.
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
            void EnsureOwner()
            {
                sourceGuard.EnsureCurrent();
                if (owner is not null) connections!.RequireProjectOwner(owner, CancellationToken.None);
            }
            void Ensure()
            {
                EnsureOwner();
                using var scope = db.OpenSnapshot(CancellationToken.None);
                if (roots.Find(scope, id) != project) throw Changed();
            }
            Ensure();
            return new(project, repository, source, owner, live, sourceGuard, Ensure, EnsureOwner);
        }
        catch { sourceGuard.Dispose(); throw; }
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
        using var target = await CaptureOwnerAsync(destination, projectId, root, repository, source, call);
        call.SourceGuard = target.Retain();
    }
    internal async Task<Target> CaptureOwnerAsync(string destination, string projectId, string root, string repository, ExpectedSource source, CallContext call)
    {
        if (call.Peer is not OwnerPeer || destination != machine || source != new ExpectedSource(machine, 0, null) ||
            call.ExpectedSource is not null && call.ExpectedSource != source ||
            string.IsNullOrWhiteSpace(projectId) || !Path.IsPathFullyQualified(root)) throw Changed();
        var actualRepository = GitHubRepository.Parse(repository);
        var guard = await sources.CaptureEngineAsync(source, call.Cancellation);
        try
        {
            guard.EnsureCurrent();
            return new(new(projectId, "local", root, false), actualRepository, source, null, null, guard, guard.EnsureCurrent, guard.EnsureCurrent);
        }
        catch { guard.Dispose(); throw; }
    }
    internal sealed class Target(ProjectRoot root, string repository, ExpectedSource source,
        RemoteConnections.ProjectOwner? owner, EngineMirror.LiveMirrorBinding? live, IExpectedSourceGuard guard, Action ensure, Action ensureOwner) : IDisposable
    {
        private int _references = 1;
        internal ProjectRoot Root => root;
        internal string Repository => repository;
        internal ExpectedSource Source => source;
        internal RemoteConnections.ProjectOwner? Owner => owner;
        internal EngineMirror.LiveMirrorBinding? Live => live;
        internal CancellationToken Changed => guard.Changed;
        internal void EnsureCurrent() => ensure();
        internal void EnsureOwner() => ensureOwner();
        // The queued reply and accepted execution each own a reference; neither borrows CallContext lifetime.
        internal IExpectedSourceGuard Retain() { Interlocked.Increment(ref _references); return new Reference(this, false); }
        internal IExpectedSourceGuard RetainOwner() { Interlocked.Increment(ref _references); return new Reference(this, true); }
        public void Dispose() { if (Interlocked.Decrement(ref _references) == 0) guard.Dispose(); }
        private sealed class Reference : IExpectedSourceGuard
        {
            private readonly Target _target;
            private readonly bool _ownerOnly;
            private Target? _owned;
            internal Reference(Target target, bool ownerOnly) { _target = target; _owned = target; _ownerOnly = ownerOnly; }
            public CancellationToken Changed => _target.Changed;
            public void EnsureCurrent() { if (_ownerOnly) _target.EnsureOwner(); else _target.EnsureCurrent(); }
            public void Dispose() => Interlocked.Exchange(ref _owned, null)?.Dispose();
        }
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
