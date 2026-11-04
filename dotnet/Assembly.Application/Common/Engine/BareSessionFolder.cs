namespace Assembly.Application.Common.Engine;

/// <summary>The shared working folder for new projectless sessions on their owning machine.</summary>
public static class BareSessionFolder
{
    public static string? ForHome(string? home) =>
        string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home)
            ? null : Path.Combine(home, "assembly-bare");
}
