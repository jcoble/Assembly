using Assembly.Application.Common.Git;
using Assembly.Application.Common.Calls;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>The same full porcelain stream supplies both the display cap and the mutation fingerprint. Each file also
/// gets its revision, and the fingerprint covers them, because an edit to an already changed file keeps its letters.</summary>
public sealed class GitStatusReader(IGit git)
{
    public async Task<GitStatus> ReadAsync(string root, CancellationToken ct) => (await ReadFullAsync(root, ct)).Status;

    public async Task<(GitStatus Status, IReadOnlyList<ChangedFile> All)> ReadFullAsync(string root, CancellationToken ct)
    {
        var full = await GitStatusPorcelain.ParseAsync(Records(root, ct), int.MaxValue);
        var all = full.Files.Select(file => file with { Revision = git.Revision(root, file.Path) }).ToArray();
        return (full with { Files = all.Take(1000).ToArray(), Fingerprint = Fingerprint(full.Fingerprint, all), CheckoutRoot = Path.GetFullPath(root) }, all);
    }
    private static string Fingerprint(string records, IEnumerable<ChangedFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(records));
        foreach (var file in files) hash.AppendData(Encoding.UTF8.GetBytes($"\0{file.Path}\0{file.Revision}"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
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
