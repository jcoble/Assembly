using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Projects;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine.Workflows;

public sealed record WorkflowDefinitionsAtRootArgs(string Machine, string ProjectRoot, string? After = null, int Limit = 50);

internal sealed partial class WorkflowCalls
{
    private void RegisterDefinitions(ICallRegistry calls)
    {
        calls.Read("workflow", "definitions", WorkflowReadCallsJson.Default.WorkflowDefinitionsArgs,
            WorkflowReadCallsJson.Default.JsonElement, DefinitionsAsync, bulk: true);
        calls.Read("workflow", "definitions_at_root", WorkflowReadCallsJson.Default.WorkflowDefinitionsAtRootArgs,
            WorkflowReadCallsJson.Default.JsonElement, (args, call) =>
            {
                Owner(call);
                if (args.Machine != runtime.Machine) throw new CallFailed("Wrong owning engine machine.", "wrong_machine");
                return Task.FromResult(DefinitionPage(args.ProjectRoot, args.After, args.Limit, call.Cancellation));
            }, bulk: true);
    }

    private async Task<JsonElement> DefinitionsAsync(WorkflowDefinitionsArgs args, CallContext call)
    {
        Owner(call);
        if (string.IsNullOrWhiteSpace(args.Project)) throw new CallFailed("Select a project before listing workflow definitions.", "project_required");
        if (args.Machine is not null && string.IsNullOrWhiteSpace(args.Machine)) throw new CallBadArguments("wrong_machine");
        var registry = projects ?? throw new CallFailed("The project registry is unavailable.", "project_required");
        var project = registry.Get(args.Project);
        if (project is null || project.RemovedAt is not null) throw new KeyNotFoundException("The selected project is no longer available.");
        void CheckProject()
        {
            var current = registry.Get(project.Id);
            if (current is null || current.RemovedAt is not null || current.RootPath != project.RootPath || current.Machine != project.Machine)
                throw DefinitionChanged();
        }
        if (project.Machine == "local")
        {
            if (args.Machine is not null && args.Machine != runtime.Machine) throw new CallBadArguments("wrong_machine");
            var guard = new DefinitionSource(CancellationToken.None, CheckProject);
            call.SourceGuard = guard; guard.EnsureCurrent();
            var root = (resolveProject ?? throw new CallFailed("The project root resolver is unavailable.", "project_required"))(project.Id);
            if (root != project.RootPath) throw DefinitionChanged();
            var page = DefinitionPage(root, args.After, args.Limit, call.Cancellation);
            guard.EnsureCurrent(); return page;
        }
        if (connections is null || forward is null) throw new CallFailed("The selected project's owning machine is unavailable.", "project_owner_unavailable");
        RemoteConnections.ProjectOwner owner;
        try { owner = await connections.CaptureProjectOwnerAsync(project.Machine, call.Cancellation).ConfigureAwait(false); }
        catch (CallFailed error) when (error.Reason == "project_owner_changed") { throw DefinitionChanged(); }
        if (args.Machine is not null && args.Machine != owner.Engine) throw new CallBadArguments("wrong_machine");
        void CheckOwner()
        {
            CheckProject();
            try { connections.RequireProjectOwner(owner, CancellationToken.None); }
            catch (CallFailed) { throw DefinitionChanged(); }
        }
        var remoteGuard = new DefinitionSource(owner.Binding.Invalidated, CheckOwner);
        call.SourceGuard = remoteGuard;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, remoteGuard.Changed);
        remoteGuard.EnsureCurrent();
        var result = await forward.WorkflowDefinitionsAtRootAsync(owner,
            new(owner.Engine, project.RootPath, args.After, args.Limit), stop.Token).ConfigureAwait(false);
        remoteGuard.EnsureCurrent(); return result;
    }

    private static JsonElement DefinitionPage(string root, string? after, int limit, CancellationToken ct)
    {
        try
        {
            return JsonSerializer.SerializeToElement(WorkflowDefinitionDirectory.List(root, after, limit, ct),
                WorkflowDefinitionReadJson.Default.WorkflowDefinitionPage);
        }
        catch (ArgumentException error) { throw new CallBadArguments(error.Message); }
        catch (WorkflowFieldException error) { throw new CallBadArguments(error.Message); }
    }

    private static void Owner(CallContext call)
    {
        if (call.Peer is not OwnerPeer) throw new CallFailed("Forbidden.", "forbidden");
        call.Cancellation.ThrowIfCancellationRequested();
    }

    private sealed class DefinitionSource(CancellationToken changed, Action check) : IExpectedSourceGuard
    {
        private int disposed;
        public CancellationToken Changed => changed;
        public void EnsureCurrent()
        {
            if (Volatile.Read(ref disposed) != 0 || changed.IsCancellationRequested) throw DefinitionChanged();
            check();
        }
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
    }

    private static CallFailed DefinitionChanged() => new("The selected workflow definition source changed.", "source_changed");
}
