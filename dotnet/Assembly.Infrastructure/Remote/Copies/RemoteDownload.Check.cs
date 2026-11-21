using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;

namespace Assembly.Infrastructure.Remote.Copies;

/// <summary>
/// The one hole check that makes a downloaded copy whole (TSK-1692). Once the download reaches the remote's oldest row,
/// the rows saved here up to the copy's head are counted against the remote's count. Equal counts finish the copy with
/// no call. Otherwise each jump in the saved seqs is read again with <c>rows/after</c>, then the copy is finished.
/// </summary>
internal sealed partial class RemoteDownload
{
    // Adopted copies waiting for the follower to reach the remote's head, each with that head; under its own lock.
    private readonly Dictionary<string, long> _awaitingFollower = [];

    /// <summary>
    /// The follower caught the session up to <paramref name="lastSeq"/>. An adopted copy waiting for that is queued for
    /// its check. Called on the follower's own task, so it only queues.
    /// </summary>
    public void CaughtUp(string session, long lastSeq)
    {
        lock (_awaitingFollower)
        {
            if (!_awaitingFollower.TryGetValue(session, out var head) || lastSeq < head) return;
            _awaitingFollower.Remove(session);
        }
        Queue(session);
    }

    // Finishes the copy, or leaves it waiting for the follower when the rows up to the remote's head aren't here yet.
    // A pass cut short writes nothing more: the copy stays downloading and the next connection runs the check again.
    private async Task CheckAsync(string session, CancellationToken ct)
    {
        if (_hub.Copies.RemoteCopyOf(session) is not { Complete: false, Head: { } head }) return;
        if (_hub.LastSeq(session) < head)
        {
            lock (_awaitingFollower) _awaitingFollower[session] = head;
            return;
        }
        var expected = _hub.Copies.RemoteCount(session);
        if (expected is { } count && count == _hub.Copies.SavedCount(session, head))
        {
            _hub.CompleteCopy(session);
            return;
        }
        // One reply can reach past several jumps; a jump it reached is skipped.
        var reached = 0L;
        foreach (var (from, to) in _hub.Copies.Jumps(session, head))
        {
            for (var at = Math.Max(from, reached); at < to - 1 && at < head;)
            {
                var rows = await AfterAsync(session, at, ct);
                if (rows.Count == 0) break;
                foreach (var row in _hub.Backfill(session, [.. rows.Where(row => row.Seq <= head)])) _subagentCopied(session, row);
                at = reached = rows[^1].Seq;
            }
        }
        if (expected != _hub.Copies.SavedCount(session, head))
            _log.WriteLine($"remote session {session}: {_hub.Copies.SavedCount(session, head)} rows saved up to {head}, the remote has {expected}");
        _hub.CompleteCopy(session);
    }

    private async Task<IReadOnlyList<StoredRow>> AfterAsync(string session, long after, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize($$"""{"session":{{JsonSerializer.Serialize(session, RowJsonContext.Rows.String)}},"after":{{after}},"limit":{{RowsFeature.PageLimit}}}""",
            RowJsonContext.Rows.JsonElement);
        var reply = await _bulkCall("after", args, ct);
        return [.. reply.GetProperty("rows").EnumerateArray().Select(RowWire.Read)];
    }
}
