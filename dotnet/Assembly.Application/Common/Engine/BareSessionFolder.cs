namespace Assembly.Application.Common.Engine;

/// <summary>The shared working folder for new projectless sessions on their owning machine.</summary>
public static class BareSessionFolder
{
    // SQL callers use this same predicate for Engine display, the rail and the legacy projection.
    public static string Predicate(string project, string folder) =>
        $"COALESCE(({project} IS NULL AND {folder} = (SELECT bare_folder FROM engine_meta WHERE singleton=1)),0)";

    public static string? ForHome(string? home) =>
        string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home)
            ? null : Path.Combine(home, "assembly-bare");
}
