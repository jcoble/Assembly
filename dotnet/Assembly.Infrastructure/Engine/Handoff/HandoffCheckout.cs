using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.SourceControl;

namespace Assembly.Infrastructure.Engine.Handoff;

/// <summary>Owns staging until a verified checkout is atomically published; never repairs an existing checkout.</summary>
internal static class HandoffCheckout
{
    private const string OwnerFile = "assembly-handoff";

    internal static async Task<string> PrepareAsync(string checkouts, string handoffId, WorkspaceCheck source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Controller B's implementation mapping: invalid inputs/protected content use existing bad_args.
        if (handoffId.Length != 36 || !Guid.TryParseExact(handoffId, "D", out var id) || !HandoffGit.IsBranch(source.Branch) ||
            !HandoffGit.IsOid(source.Head) || !HandoffGit.IsUrl(source.RepoUrl) || HandoffGit.IsRelativeFileUrl(source.RepoUrl)) throw new HandoffRefused("bad_args");
        var identity = id.ToString("D") + "\n" + Convert.ToBase64String(Encoding.UTF8.GetBytes(source.RepoUrl)) + "\n" +
            source.Branch + "\n" + source.Head + "\n";
        string? reservation = null, partial = null;
        var ownedPartial = false;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkouts));
            HandoffGit.NoLinks(root);
            Directory.CreateDirectory(root);
            HandoffGit.NoLinks(root);
            var target = Path.Combine(root, HandoffGit.RepoName(source.RepoUrl) + "-" + id.ToString("N")[..8]);
            HandoffGit.NoLinks(target);
            if (Path.Exists(target))
            {
                await VerifyFinal(target, identity, source, ct);
                return target;
            }
            partial = target + ".partial";
            HandoffGit.NoLinks(partial);
            if (Path.Exists(partial)) throw new HandoffRefused("bad_args");
            // Directory.Move reserves the stable partial without overwriting a concurrent handoff's reservation.
            reservation = Path.Combine(root, ".handoff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(reservation);
            await WriteOwner(reservation, identity);
            Directory.Move(reservation, partial);
            reservation = null;
            ownedPartial = true;
            var repository = Path.Combine(partial, "repository");
            try
            {
                await HandoffGit.Records(partial, ["clone", "--quiet", "--no-checkout", "--single-branch", "--no-tags",
                    "--branch", source.Branch, "--", source.RepoUrl, repository], ct);
                var advertised = await HandoffGit.Scalar(repository,
                    ["rev-parse", "--verify", "refs/remotes/origin/" + source.Branch + "^{commit}"], ct);
                if (advertised != source.Head || !HandoffGit.IsOid(advertised) ||
                    await HandoffGit.Scalar(repository, ["cat-file", "-t", source.Head], ct) != "commit")
                    throw new HandoffRefused("head_not_found");
                await HandoffGit.Records(repository, ["checkout", "--quiet", source.Branch, "--"], ct);
                // The clone's requested branch must still point at the advertised exact commit after checkout.
                await WriteOwner(Path.Combine(repository, ".git"), identity);
                await VerifyFinal(repository, identity, source, ct);
            }
            catch (CallFailed) { throw new HandoffRefused("head_not_found"); }
            ct.ThrowIfCancellationRequested();
            HandoffGit.NoLinks(target);
            Directory.Move(repository, target);
            return target;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new HandoffRefused("bad_args");
        }
        finally
        {
            // GitCli's finally has already killed and waited for its exact process before cleanup is reached.
            if (ownedPartial) Cleanup(partial!, identity);
            if (reservation is not null) Cleanup(reservation, identity);
        }
    }

    private static async Task VerifyFinal(string target, string identity, WorkspaceCheck source, CancellationToken ct)
    {
        var owner = Path.Combine(target, ".git", OwnerFile);
        HandoffGit.NoLinks(owner);
        if (!File.Exists(owner) || await File.ReadAllTextAsync(owner, ct) != identity) throw new HandoffRefused("bad_args");
        try
        {
            var status = await new GitStatusReader(HandoffGit.Cli).ReadAsync(target, ct);
            if (status.TotalFiles != 0 || status.Branch != source.Branch || status.Head != source.Head ||
                await HandoffGit.Scalar(target, ["remote", "get-url", "--all", "origin"], ct) != source.RepoUrl ||
                await HandoffGit.Scalar(target, ["cat-file", "-t", source.Head], ct) != "commit")
                throw new HandoffRefused("bad_args");
        }
        catch (CallFailed) { throw new HandoffRefused("bad_args"); }
    }

    private static async Task WriteOwner(string folder, string identity)
    {
        var temporary = Path.Combine(folder, OwnerFile + ".tmp");
        await File.WriteAllTextAsync(temporary, identity);
        File.Move(temporary, Path.Combine(folder, OwnerFile));
    }

    private static void Cleanup(string folder, string identity)
    {
        HandoffGit.NoLinks(folder);
        var owner = Path.Combine(folder, OwnerFile);
        HandoffGit.NoLinks(owner);
        if (File.Exists(owner) && File.ReadAllText(owner) == identity) Directory.Delete(folder, recursive: true);
    }
}
