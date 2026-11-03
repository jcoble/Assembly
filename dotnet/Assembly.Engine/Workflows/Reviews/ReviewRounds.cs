namespace Assembly.Engine.Workflows.Reviews;

/// <summary>Preserves the old workflow's trimmed, ordinal convergence rule.</summary>
public static class ReviewRounds
{
    public static bool ShouldPause(IReadOnlyList<string> previous, IReadOnlyList<string> current, int round, int maxRounds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(round);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRounds, 1);
        if (round >= maxRounds) return true;
        var earlier = previous.Select(text => text.Trim()).ToHashSet(StringComparer.Ordinal);
        return current.Any(text => text.Trim().StartsWith("REPEAT:", StringComparison.Ordinal) || earlier.Contains(text.Trim()));
    }
}
