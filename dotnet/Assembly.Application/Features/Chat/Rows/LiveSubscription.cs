using System.Threading.Channels;
using Assembly.Application.Features.Chat;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>
/// One socket's view of a thread: the rows it missed, then every row committed after them, in seq order.
/// <see cref="Rows"/> ends with an error if the socket falls too far behind; the webview then reopens from its watermark.
/// </summary>
public sealed class LiveSubscription : IDisposable
{
    private readonly Action<LiveSubscription> _unsubscribe;

    internal LiveSubscription(string sessionId, IReadOnlyList<StoredRow> backlog, Channel<StoredRow> queue, Action<LiveSubscription> unsubscribe)
    {
        SessionId = sessionId;
        Backlog = backlog;
        Queue = queue;
        _unsubscribe = unsubscribe;
    }

    public string SessionId { get; }

    /// <summary>The saved rows after the seq the socket asked for.</summary>
    public IReadOnlyList<StoredRow> Backlog { get; }

    /// <summary>Rows committed after the backlog was read.</summary>
    public ChannelReader<StoredRow> Rows => Queue.Reader;

    internal Channel<StoredRow> Queue { get; }

    public void Dispose() => _unsubscribe(this);
}
