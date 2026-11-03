using System.Security.Cryptography;
using Assembly.Application.Common.Git;
using Assembly.Infrastructure.Git;

namespace Assembly.Infrastructure.Engine.Workflows;

internal sealed record ReceiptCheckoutState(string Head, string Fingerprint, bool Clean);

/// <summary>Dispatch and checking use the same content fingerprint, including dirty and untracked bytes.</summary>
internal sealed class ReceiptCheckout(IGit? git = null)
{
    private readonly IGit _git = git ?? new GitCli();

    internal async Task<ReceiptCheckoutState> Capture(string folder, CancellationToken ct)
    {
        var head = await Scalar(folder, ["rev-parse", "--verify", "HEAD^{commit}"], ct);
        var status = await Records(folder, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], ct);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ReceiptFiles.HashText(hash, "workflow-checkout-fingerprint/v1"); ReceiptFiles.HashText(hash, head);
        foreach (var record in status) ReceiptFiles.HashText(hash, record);
        // Tracked and nonignored untracked paths come from Git, so .git and ignored proof/build output are excluded.
        var paths = await Records(folder, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], ct);
        foreach (var path in paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested(); ReceiptFiles.HashText(hash, path);
            var full = Path.Combine(folder, path);
            if (!File.Exists(full)) { ReceiptFiles.HashText(hash, "missing"); continue; }
            var info = new FileInfo(full);
            if (info.LinkTarget is { } target)
            {
                // Hash link identity without following arbitrary targets while fingerprinting the checkout.
                ReceiptFiles.HashText(hash, "link"); ReceiptFiles.HashText(hash, target); continue;
            }
            var observed = await ReceiptFiles.Observe(folder, path, "workspace", "fingerprint", ct);
            ReceiptFiles.HashText(hash, "file"); ReceiptFiles.HashText(hash, observed.Sha256);
            ReceiptFiles.HashText(hash, observed.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return new(head, Convert.ToHexStringLower(hash.GetHashAndReset()), status.Count == 0);
    }

    internal async Task<IReadOnlyList<string>> FirstParentCommits(string folder, string baseHead, string head, CancellationToken ct)
    {
        if (!HexHead(baseHead) || !HexHead(head)) throw new IOException("attempt_base_unavailable");
        var ancestors = (await Scalar(folder, ["rev-list", head], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (!ancestors.Contains(baseHead, StringComparer.Ordinal)) throw new IOException("attempt_base_unavailable");
        return (await Scalar(folder, ["rev-list", "--first-parent", baseHead + ".." + head], ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    internal async Task<string?> ResolveCommit(string folder, string commit, CancellationToken ct)
    {
        if (commit.Length is < 7 or > 64 || !commit.All(char.IsAsciiHexDigit)) return null;
        var value = await Scalar(folder, ["rev-parse", "--verify", commit + "^{commit}"], ct);
        return HexHead(value) ? value : null;
    }

    internal async Task<IReadOnlySet<string>> Ancestors(string folder, string head, CancellationToken ct) =>
        (await Scalar(folder, ["rev-list", head], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    private async Task<List<string>> Records(string folder, string[] args, CancellationToken ct)
    {
        List<string> result = [];
        await foreach (var value in _git.ReadAsync(folder, args, null, ct)) if (value.Length > 0) result.Add(value);
        return result;
    }
    private async Task<string> Scalar(string folder, string[] args, CancellationToken ct) => string.Concat(await Records(folder, args, ct)).TrimEnd('\r', '\n');
    private static bool HexHead(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);
}
