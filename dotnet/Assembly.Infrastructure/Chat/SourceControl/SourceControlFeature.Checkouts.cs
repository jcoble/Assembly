using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;

namespace Assembly.Infrastructure.Chat.SourceControl;

internal sealed partial class SourceControlFeature
{
    internal sealed record Checkout(string Path, string? Branch);
    internal sealed record Checkouts(string SessionRoot, IReadOnlyList<Checkout> Worktrees);

    private static bool IsCheckoutRead(string method) => method is
        "status" or "diff" or "branchDiff" or "history" or "commitFiles" or "commitDiff";

    private static void ValidateCheckoutArguments(string method, JsonElement args)
    {
        if (!IsCheckoutRead(method) && method is not ("branches" or "stashes" or "checkouts")) return;
        var selected = args.TryGetProperty("checkoutPath", out var path) && path.ValueKind != JsonValueKind.Null;
        var observed = args.TryGetProperty("expectedCheckoutRoot", out var root) && root.ValueKind != JsonValueKind.Null;
        var source = args.TryGetProperty("expectedSource", out var expected) && expected.ValueKind != JsonValueKind.Null;
        if ((selected || observed) && !IsCheckoutRead(method))
            throw new CallBadArguments("This read does not support a checkout selection.");
        if (selected != observed || (selected || method == "checkouts") && !source)
            throw new CallBadArguments("A checkout selection requires its observed root and source.");
        if (!selected && method != "checkouts") return;
        if (!args.TryGetProperty("session", out var session) || session.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(session.GetString()))
            throw new CallBadArguments("A session is required.");
        if (selected) { ValidateCheckoutPath(path); ValidateCheckoutPath(root); }
    }

    private static void ValidateCheckoutPath(JsonElement value)
    {
        var path = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\0') || !Path.IsPathFullyQualified(path) ||
            path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Any(part => part is "." or ".."))
            throw new CallBadArguments("Choose an absolute checkout path of at most 4096 characters without traversal.");
        try { _ = Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new CallBadArguments("Choose a valid absolute checkout path."); }
    }

    // This also runs in the queued reply guard: database snapshot and pure path handling only.
    // Missing/malformed recorded state must throw CallFailed, which the existing sender safely refuses.
    private string RecordedCheckoutRoot(string session)
    {
        using var scope = db.OpenSnapshot(CancellationToken.None);
        var current = sessions.Get(scope, session);
        return current is { Machine: null } ? NormalizeRecordedRoot(sessions.Root(scope, current)) : throw CheckoutRootChanged();
    }

    private static string NormalizeRecordedRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || root.Contains('\0') || !Path.IsPathFullyQualified(root)) throw CheckoutRootChanged();
        try { return Path.GetFullPath(root); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw CheckoutRootChanged(); }
    }

    private async Task<Checkouts> ReadCheckoutsAsync(string session, CallContext call)
    {
        var root = RecordedCheckoutRoot(session);
        RequireCheckoutDirectory(root);
        var rows = await CheckoutRows(root, call.Cancellation);
        var own = await CheckoutGitPath(root, ["rev-parse", "--show-toplevel"], call.Cancellation);
        if (RecordedCheckoutRoot(session) != root) throw CheckoutRootChanged();
        RequireCheckoutDirectory(root);
        call.SourceGuard?.EnsureCurrent();
        return new(root, rows.Where(row => !row.IsBare && !row.Prunable && NormalizeListedPath(row.Path) != own)
            .Select(row => new Checkout(row.Path, row.Branch)).ToArray());
    }

    private async Task<TResult> ReadCheckoutAsync<TResult>(string session, string? selected, string? expectedRoot,
        CallContext call, Func<string, Task<TResult>> read)
    {
        // The ordinary path incurs no new Git commands or inspection policy.
        if (selected is null) return await read(SessionPaths.RootOf(sessions, session));
        var root = RecordedCheckoutRoot(session);
        if (root != NormalizeRecordedRoot(expectedRoot)) throw CheckoutRootChanged();
        var checkout = NormalizeListedPath(selected);
        await ValidateCheckout(session, root, checkout, call);
        var result = await read(checkout);
        await ValidateCheckout(session, root, checkout, call);
        // Acceptance of this readonly observation. Filesystem changes after this point are not a write lease.
        return result;
    }

    private async Task ValidateCheckout(string session, string root, string checkout, CallContext call)
    {
        if (RecordedCheckoutRoot(session) != root) throw CheckoutRootChanged();
        call.SourceGuard?.EnsureCurrent();
        RequireCheckoutDirectory(root);
        RequireCheckoutDirectory(checkout, rejectLinks: true);
        // Four fixed commands per pass, independent of the number of worktrees; never enrich every row.
        var rows = await CheckoutRows(root, call.Cancellation);
        var own = await CheckoutGitPath(root, ["rev-parse", "--show-toplevel"], call.Cancellation);
        if (checkout == own || !rows.Any(row => !row.IsBare && !row.Prunable && NormalizeListedPath(row.Path) == checkout))
            throw CheckoutUnavailable();
        var common = await CheckoutGitPath(root, ["rev-parse", "--path-format=absolute", "--git-common-dir"], call.Cancellation);
        var selectedCommon = await CheckoutGitPath(checkout, ["rev-parse", "--path-format=absolute", "--git-common-dir"], call.Cancellation);
        if (common != selectedCommon) throw CheckoutUnavailable();
        RequireCheckoutDirectory(root);
        RequireCheckoutDirectory(checkout, rejectLinks: true);
        if (RecordedCheckoutRoot(session) != root) throw CheckoutRootChanged();
        call.SourceGuard?.EnsureCurrent();
    }

    private async Task<IReadOnlyList<Worktree>> CheckoutRows(string root, CancellationToken ct)
    {
        var output = await CheckoutGit(root, ["worktree", "list", "--porcelain", "-z"], ct);
        return WorktreePorcelain.Parse(string.Join('\0', output));
    }

    private async Task<string> CheckoutGitPath(string root, string[] args, CancellationToken ct)
    {
        var output = string.Concat(await CheckoutGit(root, args, ct));
        // One command/one pathname. Remove only Git's delimiter; preserve whitespace/newlines in the actual path.
        if (output.EndsWith('\n')) output = output[..^1];
        return NormalizeListedPath(output);
    }

    private async Task<List<string>> CheckoutGit(string root, string[] args, CancellationToken ct)
    {
        try
        {
            var records = new List<string>();
            await foreach (var record in git.ReadAsync(root, args, null, ct)) records.Add(record);
            return records;
        }
        catch (Exception error) when (error is CallFailed or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { throw CheckoutUnavailable(); }
    }

    private static string NormalizeListedPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path)) throw CheckoutUnavailable();
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw CheckoutUnavailable(); }
    }

    private static void RequireCheckoutDirectory(string path, bool rejectLinks = false)
    {
        try
        {
            if (!Directory.Exists(path)) throw CheckoutUnavailable();
            if (!rejectLinks) return;
            for (string? at = path; at is not null; at = Path.GetDirectoryName(at))
                if (new DirectoryInfo(at).LinkTarget is not null) throw CheckoutUnavailable();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw CheckoutUnavailable(); }
    }

    private static CallFailed CheckoutRootChanged() => new("The session checkout changed. Refresh its checkouts.", "checkout_root_changed");
    private static CallFailed CheckoutUnavailable() => new("The checkout is unavailable. Refresh its checkouts.", "checkout_unavailable");
}
