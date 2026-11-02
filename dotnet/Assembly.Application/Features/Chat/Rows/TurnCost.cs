namespace Assembly.Application.Features.Chat.Rows;

/// <summary>The existing chat Stats cost baseline, shared with final provider measurements.</summary>
internal sealed class TurnCost(decimal? initial)
{
    private decimal? _baseline = initial;
    private bool _unknown;
    public void AgentStarted(bool resumed, decimal? saved)
    {
        _baseline = resumed ? saved : null;
        _unknown = resumed && saved is null;
    }
    public decimal? Delta(string status, decimal? current) =>
        _unknown || status == "failed" ? null : current - (_baseline ?? 0);
}
