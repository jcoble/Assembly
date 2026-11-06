namespace Assembly.Application.Common.GitHub;

/// <summary>The CLI started, but its completion could not be established. Writes must never be replayed.</summary>
public sealed class GhInterrupted(string message, Exception inner, CancellationToken cancellationToken)
    : OperationCanceledException(message, inner, cancellationToken);
