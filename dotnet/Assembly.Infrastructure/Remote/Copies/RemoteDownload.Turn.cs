using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Rows;

namespace Assembly.Infrastructure.Remote.Copies;

/// <summary>
/// A finished run, read again from the remote (TSK-1692): its user rows' acknowledgment and send fields are written in
/// place on the remote after the rows were copied here, and its tools' details never travel on the rows topic, so the
/// saved copy takes both at turn end.
/// </summary>
internal sealed partial class RemoteDownload
{
    /// <summary>
    /// A copied turn ended: its run is read again on the download loop, after any work queued before it.
    /// Only a session with a saved copy here is read.
    /// </summary>
    public void TurnEnded(string session, string run)
    {
        if (_hub.Copies.RemoteCopyOf(session) is not null) _work.Writer.TryWrite(new(session, run));
    }

    private async Task RereadTurnAsync(string session, string run, CancellationToken ct)
    {
        try
        {
            // Each frame holds as many tool details as fit; Next reads on from the last one it held.
            long? after = null;
            do
            {
                var reply = await _bulkCall("turn", JsonSerializer.SerializeToElement(new RowsFeature.TurnArgs(session, run, after),
                    RowJsonContext.Rows.TurnArgs), ct);
                var turn = reply.Deserialize(RowJsonContext.Rows.TurnRows) ?? throw new CallFailed("incompatible backend");
                _hub.SaveTurn(session, [.. turn.Users.Select(RowWire.Read)], turn.Details);
                after = turn.Next;
            }
            while (after is not null);
        }
        catch (Exception error) when (Failed(error, ct))
        {
            _log.WriteLine($"remote session {session}: run {run} not read again: {error.Message}");
        }
    }
}
