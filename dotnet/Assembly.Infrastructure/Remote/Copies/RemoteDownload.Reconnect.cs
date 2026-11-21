using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Rows.Copies;

namespace Assembly.Infrastructure.Remote.Copies;

/// <summary>
/// What each connection does with the saved copies (TSK-1692): one <c>rows/heads</c> call for every copy of this
/// machine, then only the sessions that changed are followed, downloaded again or backfilled.
/// </summary>
internal sealed partial class RemoteDownload
{
    private async Task ReconnectAsync(CancellationToken ct)
    {
        string machine;
        IReadOnlyDictionary<string, HistoryFacts> heads;
        try
        {
            // With nothing saved here there is nothing to decide, and no call is made.
            if (!_hub.Copies.Any()) return;
            machine = await _verifiedEngine(ct);
            // A machine replaced under the same name: its copies were saved from another engine.
            foreach (var session in _hub.Copies.MovedTo(machine))
            {
                var open = _hub.HasSubscribers(session);
                await DropAsync(session);
                if (open) Queue(session);
            }
            var copies = _hub.Copies.RemoteCopiesOn(machine);
            if (copies.Count == 0) return;
            heads = await HeadsAsync([.. copies.Select(copy => copy.Session)], ct);
            foreach (var copy in copies) await DecideAsync(copy, machine, heads.GetValueOrDefault(copy.Session));
        }
        catch (Exception error) when (Failed(error, ct)) { _log.WriteLine($"remote downloads: {error.Message}"); }
    }

    // One saved copy against the remote's facts for it. A session the remote has no rows for is left as it is.
    private async Task DecideAsync(RemoteCopy copy, string machine, HistoryFacts? facts)
    {
        if (facts is null)
        {
            if (!copy.Complete) Queue(copy.Session);
            return;
        }
        var last = _hub.LastSeq(copy.Session);
        if (facts.Head < last)
        {
            await DropAsync(copy.Session);
            Queue(copy.Session);
            return;
        }
        if (facts.Head > last) _follow(copy.Session);
        if (copy.Head is null)
        {
            // Adopted at startup: its check waits until the follower reaches this head.
            _hub.AdoptCopy(copy.Session, machine, facts);
            Queue(copy.Session);
        }
        else if (_hub.Copies.FirstSeq(copy.Session) is { } oldest && facts.Low > 0 && facts.Low < oldest)
        {
            _hub.ReopenCopy(copy.Session, facts);
            Queue(copy.Session);
        }
        else if (!copy.Complete) Queue(copy.Session);
    }

    // The follower stops first, so nothing saves into the copy while it goes. A download of it is never running here:
    // this runs on the download loop, and a queued one starts again from the top.
    private async Task DropAsync(string session)
    {
        lock (_awaitingFollower) _awaitingFollower.Remove(session);
        await _forget(session);
        _hub.DropCopy(session);
    }

    // Queues the session's download unless it's already queued.
    private void Queue(string session)
    {
        lock (_downloading)
            if (_downloading.TryAdd(session, new(TaskCreationOptions.RunContinuationsAsynchronously)))
                _work.Writer.TryWrite(session);
    }

    private async Task<IReadOnlyDictionary<string, HistoryFacts>> HeadsAsync(IReadOnlyList<string> sessions, CancellationToken ct)
    {
        var reply = await _bulkCall("heads", JsonSerializer.SerializeToElement(new RowsFeature.HeadsArgs(sessions), RowJsonContext.Rows.HeadsArgs), ct);
        return reply.Deserialize(RowJsonContext.Rows.HeadsReply)?.Sessions ?? new Dictionary<string, HistoryFacts>();
    }
}
