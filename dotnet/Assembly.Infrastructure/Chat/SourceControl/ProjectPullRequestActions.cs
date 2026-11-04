using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Git;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Chat.SourceControl;

/// <summary>Project PR actions use the existing operation lifetime and the same captured target as project reads.</summary>
internal sealed class ProjectPullRequestActions(ProjectPullRequestReads projects, GitOperations operations,
    GitSessionOperations producer, MachineForward? forward)
{
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();

    internal void Register(ICallRegistry calls)
    {
        calls.Read("git", "project_operation_host", ProjectActionJson.Default.OwnerRequest, GitOperationsJson.Default.GitOperationHost,
            async (a, call) => { using var target = await Owner(a, call); return new(operations.InstanceId, operations.Machine); });
        calls.Read("git", "project_operation", ProjectActionJson.Default.OwnerRequest, GitOperationsJson.Default.GitOperationStatus,
            async (a, call) => { using var target = await Owner(a, call); var scope = Scope(a); operations.RequireScope(scope); return operations.Status(scope.OperationId); });
        calls.Write("git", "project_start", ProjectActionJson.Default.OwnerRequest, GitOperationsJson.Default.GitOperationStatus,
            async (a, call) =>
            {
                using var target = await Owner(a, call);
                var request = a.Start ?? throw new CallBadArguments("A project action is required.");
                if (request.Action != a.Action) throw new CallFailed("forbidden");
                RequireTarget(request, target);
                var accepted = StartLocal(request, target);
                return _ => accepted;
            });
        calls.Write("git", "project_cancel", ProjectActionJson.Default.OwnerRequest, GitOperationsJson.Default.JsonElement,
            async (a, call) => { using var target = await Owner(a, call); var scope = Scope(a); operations.RequireScope(scope); operations.Cancel(scope.OperationId); return _ => Empty; });
        calls.Write("git", "project_release", ProjectActionJson.Default.OwnerRequest, GitOperationsJson.Default.JsonElement,
            async (a, call) => { using var target = await Owner(a, call); var scope = Scope(a); operations.RequireScope(scope); operations.Release(scope.OperationId); return _ => Empty; });
    }

    internal async Task<GitOperationHost> HostAsync(GitOperationHostArgs args, CallContext call)
    {
        RequireScope(args.Session, args.ProjectId, args.Action, args.Machine);
        using var target = await Capture(args.ProjectId!, args.ExpectedSource, call);
        return target.Owner is null ? new(operations.InstanceId, operations.Machine) :
            await Send(target, "project_operation_host", Descriptor(target, args.Action), GitOperationsJson.Default.GitOperationHost, call, false);
    }
    internal async Task<GitOperationStatus> StartAsync(GitOperationStart args, CallContext call)
    {
        RequireScope(args.Session, args.ProjectId, args.Action, args.Machine);
        using var target = await Capture(args.ProjectId!, args.ExpectedSource, call);
        RequireTarget(args, target);
        if (target.Owner is null) return StartLocal(args, target);
        var ownerArgs = args with { ExpectedSource = new(target.Source.Machine, 0, null) };
        await operations.WaitRemoteProjectObservationAsync(target.Source.Machine, args, target, call.Cancellation);
        return operations.RecordRemoteStatus(await Send(target, "project_start", Descriptor(target, args.Action) with { Start = ownerArgs },
            GitOperationsJson.Default.GitOperationStatus, call, true));
    }
    internal async Task<GitOperationStatus> StatusAsync(GitOperationArgs args, CallContext call)
    {
        RequireScope(args.Session, args.ProjectId, args.Action, args.Machine);
        using var acceptedScope = operations.RetainAcceptedProject(args, out var target);
        call.SourceGuard = target.RetainOwner();
        if (target.Owner is null) { operations.RequireScope(args); return operations.Status(args.OperationId); }
        return operations.RecordRemoteStatus(await Send(target, "project_operation", Descriptor(target, args.Action) with { Operation = args with { ExpectedSource = new(target.Source.Machine, 0, null) } },
            GitOperationsJson.Default.GitOperationStatus, call, false, ownerOnly: true));
    }
    internal async Task<JsonElement> StopAsync(GitOperationArgs args, CallContext call, bool release)
    {
        RequireScope(args.Session, args.ProjectId, args.Action, args.Machine);
        using var acceptedScope = operations.RetainAcceptedProject(args, out var target);
        call.SourceGuard = target.RetainOwner();
        if (target.Owner is null)
        {
            operations.RequireScope(args);
            if (release) operations.Release(args.OperationId); else operations.Cancel(args.OperationId);
            return Empty;
        }
        return await Send(target, release ? "project_release" : "project_cancel", Descriptor(target, args.Action) with { Operation = args with { ExpectedSource = new(target.Source.Machine, 0, null) } },
            GitOperationsJson.Default.JsonElement, call, true, ownerOnly: true);
    }

    private async Task<ProjectPullRequestReads.Target> Capture(string id, ExpectedSource? source, CallContext call)
    {
        var target = await projects.CaptureAsync(id, source ?? throw Changed(), call);
        call.SourceGuard = target.Retain();
        return target;
    }
    private async Task<ProjectPullRequestReads.Target> Owner(OwnerRequest args, CallContext call)
    {
        if (!GitOperations.ProjectPrAction(args.Action)) throw new CallFailed("forbidden");
        var target = await projects.CaptureOwnerAsync(args.TargetMachine, args.ProjectId, args.Root, args.Repository, args.ExpectedSource, call);
        call.SourceGuard = target.Retain();
        return target;
    }
    private GitOperationStatus StartLocal(GitOperationStart request, ProjectPullRequestReads.Target target)
    {
        var execute = producer.PrepareProject(request, target.Root.RootPath, target.Repository, target.EnsureCurrent);
        return operations.StartProject(request, async ct =>
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, target.Changed);
            return await execute(stop.Token);
        }, target.Retain(), target);
    }
    private async Task<T> Send<T>(ProjectPullRequestReads.Target target, string method, OwnerRequest request,
        JsonTypeInfo<T> output, CallContext call, bool write, bool ownerOnly = false)
    {
        void Ensure() { if (ownerOnly) target.EnsureOwner(); else target.EnsureCurrent(); }
        Ensure();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, target.Changed);
        JsonElement answer;
        try
        {
            answer = await forward!.CallAsync(target.Source.Machine, "git", method,
                JsonSerializer.SerializeToElement(request, ProjectActionJson.Default.OwnerRequest), false,
                stop.Token, target.Owner!.Socket, target.Owner.Binding, target.Live, write);
        }
        catch (MachineUnreachable error) when (write && error.MayHaveBeenSent) { throw new CallUncertain(target.Source.Machine); }
        try { Ensure(); }
        catch (Exception) when (write) { throw new CallUncertain(target.Source.Machine); }
        return answer.Deserialize(output) ?? throw Changed();
    }
    private static void RequireScope(string? session, string? project, string? action, string? machine)
    {
        if (session is not null || string.IsNullOrWhiteSpace(project) || machine is not null || !GitOperations.ProjectPrAction(action)) throw new CallFailed("forbidden");
    }
    private static void RequireTarget(GitOperationStart request, ProjectPullRequestReads.Target target)
    {
        RequireScope(request.Session, request.ProjectId, request.Action, request.Machine);
        if (request.ProjectId != target.Root.Id || request.ExpectedSource != target.Source || request.ExpectedCheckoutRoot != target.Root.RootPath ||
            !string.Equals(request.ExpectedRepository, target.Repository, StringComparison.OrdinalIgnoreCase)) throw Changed();
    }
    private static GitOperationArgs Scope(OwnerRequest args)
    {
        var scope = args.Operation ?? throw new CallBadArguments("An operation is required.");
        RequireScope(scope.Session, scope.ProjectId, scope.Action, scope.Machine);
        if (scope.ProjectId != args.ProjectId || scope.Action != args.Action || scope.ExpectedSource != args.ExpectedSource) throw Changed();
        return scope;
    }
    private static OwnerRequest Descriptor(ProjectPullRequestReads.Target target, string? action) =>
        new(target.Source.Machine, target.Root.Id, target.Root.RootPath, target.Repository, new(target.Source.Machine, 0, null), action);
    private static CallFailed Changed() => new("The project repository or owning source changed. Refresh and try again.", "source_changed");
    internal sealed record OwnerRequest([property: JsonRequired] string TargetMachine, [property: JsonRequired] string ProjectId,
        [property: JsonRequired] string Root, [property: JsonRequired] string Repository, [property: JsonRequired] ExpectedSource ExpectedSource,
        string? Action, GitOperationStart? Start = null, GitOperationArgs? Operation = null);
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProjectPullRequestActions.OwnerRequest))]
internal sealed partial class ProjectActionJson : JsonSerializerContext;
