using System.Globalization;
using System.Text.RegularExpressions;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
namespace Assembly.Application.Features.Chat.SourceControl;

public sealed record GitStash(string Sha, string Message, string? Branch, long CreatedMs, bool MadeByAssembly);
/// <summary>Stash identities are immutable SHAs; mutable indices are checked only while dropping.</summary>
public sealed class GitStashes(IGit git, TimeProvider time)
{
    private const string Changed = "The stash list changed while dropping; refresh and check Stashes.";
    private sealed record Entry(GitStash Stash, string OriginalMessage);
    public async Task<IReadOnlyList<GitStash>> ListAsync(string root, CancellationToken ct) => (await Entries(root, ct)).Select(entry => entry.Stash).ToArray();
    private async Task<IReadOnlyList<Entry>> Entries(string root, CancellationToken ct)
    {
        List<Entry> entries = [];
        await foreach (var record in git.ReadAsync(root, ["stash", "list", "--format=%H%x1f%gs%x1f%ct%x00"], null, ct))
        {
            if (string.IsNullOrWhiteSpace(record)) continue;
            var fields = record.TrimStart('\r', '\n').Split('\u001f', 3);
            if (fields.Length != 3 || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) throw new CallFailed("Git returned invalid stash metadata.");
            var match = Regex.Match(fields[1], @"^(?:WIP on|On) ([^:]+): (.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            var message = match.Success ? match.Groups[2].Value : fields[1];
            entries.Add(new(new(fields[0], message, match.Success ? match.Groups[1].Value : null, checked(seconds * 1000), message.StartsWith("assembly ", StringComparison.Ordinal)), fields[1]));
        }
        return entries;
    }
    public async Task<GitWriteResult> StashAsync(string root, string session, string text, bool includeUntracked, CancellationToken ct)
    {
        if (session.Contains('\0') || text.Contains('\0')) throw new CallFailed("A stash message cannot contain a null character.");
        // Git strips trailing whitespace from what it stores, so the message it gets is the one to look for.
        var message = $"assembly {session} {time.GetUtcNow():yyyy-MM-ddTHH:mm}: {text}".TrimEnd();
        var before = (await Entries(root, ct)).Select(entry => entry.Stash.Sha).ToHashSet(StringComparer.Ordinal);
        List<string> args = ["stash", "push"];
        if (includeUntracked) args.Add("--include-untracked");
        args.AddRange(["--message", message]);
        var run = await git.RunAsync(root, args, null, null, ct);
        if (run.ExitCode != 0) throw GitErrors.From("stash", run);
        if (run.Output.Contains("No local changes to save", StringComparison.Ordinal)) throw new CallFailed("Nothing to stash; the folder is clean.");
        var made = (await Entries(root, ct)).Where(entry => entry.Stash.Message == message && !before.Contains(entry.Stash.Sha)).ToArray();
        if (made.Length != 1) throw new CallFailed("The stash result changed. Refresh Stashes before continuing.");
        return await Result(root, "Stashed changes as " + made[0].Stash.Sha, ct);
    }
    public async Task<GitWriteResult> ApplyAsync(string root, string sha, CancellationToken ct)
    {
        Validate(sha);
        if (!(await Entries(root, ct)).Any(entry => entry.Stash.Sha == sha)) throw new CallFailed("That stash is gone already.");
        var run = await git.RunAsync(root, ["stash", "apply", sha], null, null, ct);
        if (run.ExitCode != 0) throw new CallFailed("The stash didn't apply cleanly; it's kept. Resolve the files marked ! first.\n\n" + run.Error.Trim());
        await Drop(root, sha, ct);
        return await Result(root, "Applied stash " + sha, ct);
    }
    public async Task<GitWriteResult> DropAsync(string root, string sha, CancellationToken ct)
    {
        Validate(sha); await Drop(root, sha, ct); return await Result(root, "Dropped stash " + sha, ct);
    }
    private async Task Drop(string root, string sha, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var entries = await Entries(root, ct);
            var index = entries.Select((entry, index) => (entry, index)).Where(pair => pair.entry.Stash.Sha == sha).Select(pair => pair.index).DefaultIfEmpty(-1).First();
            if (index < 0) throw new CallFailed(attempt == 0 ? "That stash is gone already." : Changed);
            var run = await git.RunAsync(root, ["stash", "drop", "stash@{" + index.ToString(CultureInfo.InvariantCulture) + "}"], null, null, ct);
            if (run.ExitCode != 0) throw GitErrors.From("stash", run);
            var match = Regex.Match(run.Output, @"Dropped stash@\{\d+\} \(([0-9a-f]{40})\)", RegexOptions.CultureInvariant);
            if (!match.Success) throw new CallFailed(Changed);
            var dropped = match.Groups[1].Value;
            if (dropped == sha) return;
            // A concurrent worktree shifted indices. Restore the actual removed immutable object before retrying.
            var known = entries.FirstOrDefault(entry => entry.Stash.Sha == dropped)?.OriginalMessage;
            if (known is null)
            {
                var subject = await git.RunAsync(root, ["show", "-s", "--format=%s", dropped], null, null, ct);
                if (subject.ExitCode != 0) throw new CallFailed(Changed);
                known = subject.Output.TrimEnd('\r', '\n');
            }
            var restored = await git.RunAsync(root, ["stash", "store", "-m", known, dropped], null, null, ct);
            if (restored.ExitCode != 0) throw new CallFailed(Changed + "\n\n" + restored.Error.Trim());
        }
        throw new CallFailed(Changed);
    }
    private async Task<GitWriteResult> Result(string root, string message, CancellationToken ct) => new(message, await new GitStatusReader(git).ReadAsync(root, ct));
    private static void Validate(string sha)
    {
        if (sha.Length != 40 || sha.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new CallFailed("Expected a stash SHA.");
    }
}
