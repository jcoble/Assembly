using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Projects;
using Assembly.Infrastructure.Git;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Projects;

/// <summary>One captured execution owner's child operation, observed by the desktop's existing operation.</summary>
internal sealed class RemoteProjectPreparation(RemoteConnections connections, RemoteConnections.ProjectOwner owner,
    GitOperations operations) : IAsyncDisposable
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<GitOperationStatus> _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _stop;
    private Task? _pump;
    private string? _instance;
    private Guid _id;
    private long _version;
    private int _started, _dispatched;

    internal async Task<FolderInspection> CreateAsync(ProjectFolderCreate request, Guid id, CancellationToken ct)
    {
        try { return await CreateOnOwnerAsync(request, id, ct); }
        catch (CallFailed failure) when (Volatile.Read(ref _dispatched) != 0 && failure.Reason == "project_owner_changed")
        { throw new CallUncertain(owner.Profile.Name); }
        catch (MachineUnreachable) when (Volatile.Read(ref _dispatched) != 0)
        { throw new CallUncertain(owner.Profile.Name); }
    }
    private async Task<FolderInspection> CreateOnOwnerAsync(ProjectFolderCreate request, Guid id, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new CallFailed("operation_id_conflict");
        _id = id;
        var host = (await ReadAsync("git", "operation_host", JsonSerializer.SerializeToElement(
            new GitOperationHostArgs(Action: "prepareProjectFolder"), GitOperationsJson.Default.GitOperationHostArgs), ct))
            .Deserialize(GitOperationsJson.Default.GitOperationHost) ?? throw new CallFailed("Invalid Git operation host.");
        if (host.Machine != owner.Engine || string.IsNullOrEmpty(host.InstanceId)) throw new CallFailed("The execution owner changed.");
        _instance = host.InstanceId;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct, owner.Binding.Invalidated);
        _pump = ObserveAsync(_stop.Token);
        await _ready.Task.WaitAsync(ct);
        connections.RequireProjectOwner(owner, ct);
        operations.FolderMutationDispatched(id);
        Interlocked.Exchange(ref _dispatched, 1);
        GitOperationStatus status;
        try
        {
            var args = new GitOperationStart(_instance, id, null, "prepareProjectFolder",
                JsonSerializer.SerializeToElement(request, ProjectsJson.Default.ProjectFolderCreate));
            status = ReadStatus(await owner.Machine.CallGitWriteAsync("start", JsonSerializer.SerializeToElement(args,
                GitOperationsJson.Default.GitOperationStart), owner.Engine, owner.Socket, ct));
            connections.RequireProjectOwner(owner, ct);
        }
        catch (MachineUnreachable error) when (error.MayHaveBeenSent)
        {
            // Reconcile only the existing operation, once, on the exact retained owner. Never resend Start.
            try { status = await StatusAsync(ct); }
            catch (Exception failure) when (failure is MachineUnreachable or CallFailed or OperationCanceledException)
            { throw new CallUncertain(owner.Profile.Name); }
        }
        Accept(status);
        if (status.State == "uncertain") throw new CallUncertain(owner.Profile.Name);
        var terminal = await _terminal.Task.WaitAsync(ct);
        connections.RequireProjectOwner(owner, ct);
        if (terminal.State == "uncertain") throw new CallUncertain(owner.Profile.Name);
        if (terminal.State != "succeeded") throw new CallFailed(terminal.Error ?? "The folder was not prepared.");
        return terminal.Result?.Deserialize(ProjectsJson.Default.FolderInspection) ?? throw new CallUncertain(owner.Profile.Name);
    }

    internal async Task<FolderInspection> InspectFinalAsync(FolderInspection prepared, CancellationToken ct)
    {
        var inspected = (await ReadAsync("folders", "inspect", JsonSerializer.SerializeToElement(new FolderPathArgs(prepared.RootPath),
            ProjectsJson.Default.FolderPathArgs), ct)).Deserialize(ProjectsJson.Default.FolderInspection)
            ?? throw new CallUncertain(owner.Profile.Name);
        var final = await StatusAsync(ct); // This read revalidates the retained native directory claim at the execution owner.
        if (final.State != "succeeded" || final.Result?.Deserialize(ProjectsJson.Default.FolderInspection) != prepared ||
            inspected.RootPath != prepared.RootPath || inspected.IsGit != prepared.IsGit || inspected.CommonDir != prepared.CommonDir ||
            inspected.RepoKey != prepared.RepoKey) throw new CallUncertain(owner.Profile.Name);
        return inspected;
    }

    internal async Task<bool> CancelAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _dispatched) == 0) return true;
        var before = await StatusAsync(ct);
        if (before.State is "succeeded" or "failed" or "cancelled") return false;
        if (before.State == "uncertain") throw new CallUncertain(owner.Profile.Name);
        connections.RequireProjectOwner(owner, ct);
        try
        {
            await owner.Machine.CallGitWriteAsync("cancel", JsonSerializer.SerializeToElement(Args(), GitOperationsJson.Default.GitOperationArgs),
                owner.Engine, owner.Socket, ct);
            connections.RequireProjectOwner(owner, ct);
            var after = await StatusAsync(ct);
            if (after.State == "uncertain") throw new CallUncertain(owner.Profile.Name);
            return after.State is "accepted" or "running" or "cancelled";
        }
        catch (MachineUnreachable) { throw new CallUncertain(owner.Profile.Name); }
    }

    private GitOperationArgs Args() => new(_id, Action: "prepareProjectFolder", InstanceId: _instance);
    private async Task<GitOperationStatus> StatusAsync(CancellationToken ct) => ReadStatus(await ReadAsync("git", "operation",
        JsonSerializer.SerializeToElement(Args(), GitOperationsJson.Default.GitOperationArgs), ct));
    private GitOperationStatus ReadStatus(JsonElement json)
    {
        var status = json.Deserialize(GitOperationsJson.Default.GitOperationStatus) ?? throw new CallUncertain(owner.Profile.Name);
        if (status.OperationId != _id || status.InstanceId != _instance || status.Machine != owner.Engine || status.Version < 0)
            throw new CallUncertain(owner.Profile.Name);
        return status;
    }
    private async Task<JsonElement> ReadAsync(string feature, string method, JsonElement args, CancellationToken ct)
    {
        connections.RequireProjectOwner(owner, ct);
        var value = await owner.Machine.CallAsync(feature, method, args, false, ct, owner.Engine, owner.Socket);
        connections.RequireProjectOwner(owner, ct);
        return value;
    }
    private void Accept(GitOperationStatus status)
    {
        long prior;
        do
        {
            prior = Interlocked.Read(ref _version);
            if (status.Version < prior) return;
        } while (Interlocked.CompareExchange(ref _version, status.Version, prior) != prior);
        if (status.State is "succeeded" or "failed" or "cancelled" or "uncertain") _terminal.TrySetResult(status);
    }
    private async Task ObserveAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in owner.Machine.GitOperationFramesAsync(_id, owner.Engine, owner.Socket, ct))
            {
                connections.RequireProjectOwner(owner, ct);
                switch (frame.GetProperty("t").GetString())
                {
                    case "state":
                        foreach (var item in frame.GetProperty("items").EnumerateArray()) Accept(ReadStatus(item.GetProperty("item")));
                        _ready.TrySetResult(); break;
                    case "change": Accept(ReadStatus(frame.GetProperty("item"))); break;
                    case "error": throw new CallUncertain(owner.Profile.Name);
                }
            }
            throw new CallUncertain(owner.Profile.Name);
        }
        catch (Exception error)
        {
            _ready.TrySetException(error); _terminal.TrySetException(error);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_stop is null) return;
        await _stop.CancelAsync();
        if (_pump is not null) await _pump;
        if (_ready.Task.IsFaulted) _ = _ready.Task.Exception;
        if (_terminal.Task.IsFaulted) _ = _terminal.Task.Exception;
        _stop.Dispose();
    }
}
