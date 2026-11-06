using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Assembly.DevSeed;

/// <summary>
/// The one folder the seed may touch: assembly-dev/worktrees/&lt;name&gt;-&lt;hash&gt; under the user's home, derived from the
/// checkout. Anything else is refused before anything is created, so no path can reach live data.
/// </summary>
public static class SeedProfile
{
    public static string For(string worktree, string userHome)
    {
        Plain(worktree);
        foreach (var marker in new[] { "desktop/Cargo.toml", "dotnet/Assembly.Desktop/Assembly.Desktop.csproj" })
            if (!File.Exists(Path.Combine(worktree, marker))) throw new InvalidDataException("Not an Assembly worktree.");
        var name = Regex.Replace(Path.GetFileName(worktree), "[^a-zA-Z0-9-]", "-");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(worktree)))[..16];
        return Path.Combine(userHome, "assembly-dev", "worktrees", name + "-" + hash);
    }

    public static string Check(string home, string worktree, string userHome)
    {
        var expected = For(worktree, userHome);
        if (home != expected) throw new InvalidDataException($"The seed only writes to {expected}, not {home}.");
        Plain(home);
        return home;
    }

    public static void PrivateDirectory(string path)
    {
        Plain(path);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void AtomicText(string path, string text)
    {
        var temporary = path + ".seeding";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }

    // No traversal and no links, so the name checked is the place written.
    private static void Plain(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Split(Path.DirectorySeparatorChar).Any(part => part is "." or ".."))
            throw new InvalidDataException("Expected an absolute path without traversal.");
        for (var at = Path.TrimEndingDirectorySeparator(path); at is not null; at = Path.GetDirectoryName(at))
            if (new FileInfo(at).LinkTarget is not null) throw new InvalidDataException("Links in the path are refused.");
    }
}
