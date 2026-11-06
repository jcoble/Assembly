using System.Security.Cryptography;
using System.Text;

namespace Assembly.DevSeed;

/// <summary>Where this seed lands and what time it calls now. Every path and time comes from here, so output follows only from these.</summary>
public sealed record SeedWorld(string Home, long NowMs)
{
    public const string Machine = "local";
    public static readonly IReadOnlyDictionary<string, string> ModelOf = new Dictionary<string, string>
    {
        ["claude"] = "claude-opus-5-5",
        ["codex"] = "gpt-5.5-codex",
    };
    public string Project(string key) => Path.Combine(Home, "projects", key);
    public string Worktree(string project, string branch) => Path.Combine(Home, "worktrees", project + "-" + branch.Replace('/', '-'));
    public string Folder(string project, string? branch) => branch is null ? Project(project) : Worktree(project, branch);
    public string Attachment(string sessionId, string file) => Path.Combine(Home, "attachments", sessionId, file);
    public long Ago(double minutes) => NowMs - (long)(minutes * 60_000);

    public static string Id(string prefix, string key) => prefix + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
    // A stable GUID, for the places the app expects one.
    public static Guid GuidOf(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(key))[..16]);
}
