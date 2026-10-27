using System.Security.Cryptography;

using Assembly.Infrastructure.Mcp;
namespace Assembly.Infrastructure.Http;

/// <summary>
/// The API token, kept in a file only this user can read, as one <c>ASSEMBLY_TOKEN=&lt;hex&gt;</c> line. The desktop's app reads
/// its backend's file, and the desktop backend reads a remote machine's over ssh. Made on first start.
/// </summary>
public static class TokenFile
{
    private const string Prefix = "ASSEMBLY_TOKEN=";

    public static string ReadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            return File.ReadLines(path).Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith(Prefix, StringComparison.Ordinal))?[Prefix.Length..]
                ?? throw new InvalidOperationException($"{path} has no {Prefix} line.");
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{Prefix}{token}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return token;
    }
}
