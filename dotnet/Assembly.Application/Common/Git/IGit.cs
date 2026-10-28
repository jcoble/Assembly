namespace Assembly.Application.Common.Git;

/// <summary>
/// Reads git's output as NUL-separated records as they come. Input records end in NUL; null closes stdin at once.
/// Exit codes 0 and 1 succeed (check-ignore returns 1 when nothing matches); above that throws CallFailed with stderr.
/// </summary>
public interface IGit
{
    IAsyncEnumerable<string> ReadAsync(string folder, IReadOnlyList<string> arguments, IReadOnlyList<string>? input, CancellationToken cancellationToken);
}
