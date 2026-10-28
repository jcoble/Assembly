namespace Assembly.Infrastructure.Chat.Files;

/// <summary>
/// One folder of a session's checkout: folders first, then files, by name. Entries git ignores, and <c>.git</c>, are
/// marked excluded and left out unless asked for. Never more than <see cref="Limit"/>, so an answer stays well inside a frame.
/// </summary>
public static class DirectoryListing
{
    public const int Limit = 2000;

    public static Task<FileListing> ListAsync(string root, string dir, bool includeExcluded, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
