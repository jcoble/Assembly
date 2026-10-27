using Assembly.Application.Common.Calls;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>
/// A session's rows over the socket (plan 5.1, 5.7, C1): the <c>rows</c> topic, keyed by session, with <c>after</c> for a
/// resume, and <c>rows/page</c> for the history above it. A resume catches up at most a page; one further behind starts
/// again from the session's last seq, and the page loads the history before it.
/// </summary>
internal sealed class RowsFeature : IFeature
{
    /// <summary>The most rows a page call reads, and the most a resume catches up.</summary>
    public const int PageLimit = 500;

    private readonly LiveHub _hub;
    private readonly RowStore _store;

    public RowsFeature(LiveHub hub, RowStore store)
    {
        _hub = hub;
        _store = store;
    }

    public void Register(ICallRegistry calls)
    {
    }
}
