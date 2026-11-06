using System.Text.Json;
using Assembly.Application.Common.Git;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;

namespace Assembly.Infrastructure.Engine.Checkouts;

/// <summary>
/// Keeps the Engine's branch record on what git really has checked out. A model's own <c>git switch</c> and a switch
/// from a viewed folder never pass through the Engine, so a finished tool call or that switch asks git again. Runs
/// only on those events, at most one read per folder at a time; nothing polls or watches the disk.
/// </summary>
internal sealed class SessionBranches(SessionCutover cutover, IGit git, TextWriter log) : IAsyncDisposable
{
    private readonly object _gate = new();
    // A folder being read, and whether something moved it again meanwhile.
    private readonly Dictionary<string, bool> _reading = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _running = new(StringComparer.Ordinal);
    // Never disposed: it holds no timer or wait handle, and a tool call can still finish while stopping.
    private readonly CancellationTokenSource _stop = new();

    public void RowsCommitted(string session, IReadOnlyList<StoredRow> rows)
    {
        if (rows.Any(CanHaveMovedTheBranch) && cutover.FolderOf(session) is { } folder) FolderChanged(folder);
    }

    /// <summary>Something may have moved the branch in this folder.</summary>
    public void FolderChanged(string folder)
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            if (_reading.ContainsKey(folder)) { _reading[folder] = true; return; }
            _reading[folder] = false;
            _running[folder] = Task.Run(() => ReadAsync(folder));
        }
    }

    private async Task ReadAsync(string folder)
    {
        try
        {
            while (true)
            {
                var head = await git.RunAsync(folder, ["symbolic-ref", "--quiet", "--short", "HEAD"], null, null, _stop.Token);
                // 1 is a detached HEAD, which has no branch. Anything else isn't a checkout to report.
                if (head.ExitCode is 0 or 1) cutover.BranchObserved(folder, head.ExitCode == 0 ? head.Output.TrimEnd('\n') : null);
                lock (_gate)
                {
                    if (!_reading[folder]) { Finished(folder); return; }
                    _reading[folder] = false;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            lock (_gate) Finished(folder);
        }
        catch (Exception error)
        {
            lock (_gate) Finished(folder);
            log.WriteLine($"reading the branch failed: {error.Message}");
        }
    }

    private void Finished(string folder)
    {
        _reading.Remove(folder);
        _running.Remove(folder);
    }

    // A finished tool call that can write, or the end of a turn.
    private static bool CanHaveMovedTheBranch(StoredRow row)
    {
        if (row.Kind is not (RowKind.Tool or RowKind.Turn) || !row.Payload.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String) return false;
        if (row.Kind == RowKind.Turn) return state.GetString() is "finished" or "failed" or "cancelled";
        return state.GetString() != "started" && (!row.Payload.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
            || kind.GetString() is not ("read" or "search" or "think" or "fetch"));
    }

    public async ValueTask DisposeAsync()
    {
        Task[] running;
        lock (_gate)
        {
            _stop.Cancel();
            running = _running.Values.ToArray();
        }
        await Task.WhenAll(running).ConfigureAwait(false);
    }
}
