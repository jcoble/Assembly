using Assembly.Application.Common.Git;
using Assembly.Application.Common.Calls;
using System.Runtime.CompilerServices;

namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>The same full porcelain stream supplies both the display cap and the mutation fingerprint.</summary>
public sealed class GitStatusReader(IGit git)
{
    public Task<GitStatus> ReadAsync(string root, CancellationToken ct) =>
        GitStatusPorcelain.ParseAsync(Records(root, ct));

    public async Task<(GitStatus Status, IReadOnlyList<ChangedFile> All)> ReadFullAsync(string root, CancellationToken ct)
    {
        var full = await GitStatusPorcelain.ParseAsync(Records(root, ct), int.MaxValue);
        return (full with { Files = full.Files.Take(1000).ToArray() }, full.Files);
    }
    private async IAsyncEnumerable<string> Records(string root, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var records = git.ReadAsync(root, ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"], null, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool more;
            try { more = await records.MoveNextAsync(); }
            catch (CallFailed failed) { throw GitErrors.From("status", failed.Message); }
            if (!more) yield break;
            yield return records.Current;
        }
    }
}
