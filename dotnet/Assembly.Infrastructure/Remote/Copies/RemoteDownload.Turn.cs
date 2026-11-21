using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Rows;

namespace Assembly.Infrastructure.Remote.Copies;

/// <summary>
/// A finished run's user rows, read again from the remote (TSK-1692): their acknowledgment and send fields are written in
/// place on the remote after the rows were copied here, so the saved copy takes them at turn end.
/// </summary>
internal sealed partial class RemoteDownload
{
    /// <summary>
    /// A copied turn ended: its run's user rows are read again on the download loop, after any work queued before it.
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
            var reply = await _bulkCall("turn", JsonSerializer.SerializeToElement(new RowsFeature.TurnArgs(session, run),
                RowJsonContext.Rows.TurnArgs), ct);
            var turn = reply.Deserialize(RowJsonContext.Rows.TurnRows) ?? throw new CallFailed("incompatible backend");
            _hub.UpdateUserPayloads(session, [.. turn.Users.Select(RowWire.Read)]);
        }
        catch (Exception error) when (Failed(error, ct))
        {
            _log.WriteLine($"remote session {session}: run {run} not read again: {error.Message}");
        }
    }
}
