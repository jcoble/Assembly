namespace Assembly.Infrastructure.Chat.Files;

/// <summary>
/// Runs git in a folder and reads its NUL-separated output as it comes. The process is killed when the token fires or
/// the reader stops early, so a search nobody wants doesn't keep walking.
/// </summary>
internal static class GitLines
{
    public static IAsyncEnumerable<string> ReadAsync(string folder, IReadOnlyList<string> arguments, IEnumerable<string>? input,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
