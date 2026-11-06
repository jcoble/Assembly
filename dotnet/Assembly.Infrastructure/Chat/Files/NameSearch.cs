namespace Assembly.Infrastructure.Chat.Files;

/// <summary>Literal filename matching used by the one owning SearchRun iterator.</summary>
public static class NameSearch
{
    public static bool Matches(string name, string query) => name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
}
