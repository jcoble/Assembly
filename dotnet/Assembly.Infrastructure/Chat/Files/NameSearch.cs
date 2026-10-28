namespace Assembly.Infrastructure.Chat.Files;

/// <summary>
/// Finds files and folders whose name holds the query, ignoring case, over what git lists in the checkout (tracked
/// and untracked files, not ignored ones). A page starts after <c>cursor</c> matches and stops the walk once it knows
/// whether another page follows.
/// </summary>
public static class NameSearch
{
    public const int MaxPageSize = 200;

    public static Task<SearchPage> PageAsync(string root, string query, int cursor, int pageSize, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
