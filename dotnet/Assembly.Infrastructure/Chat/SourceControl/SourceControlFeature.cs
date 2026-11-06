using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
using Assembly.Application.Common.Projects;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.SourceControl;
using Assembly.Infrastructure.Chat;
using Assembly.Infrastructure.Git;
using Assembly.Application.Common.Database;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Chat.SourceControl;

/// <summary>Reads a session's git status, file diffs and branch diff over the socket from its local folder.</summary>
internal sealed partial class SourceControlFeature(SessionStore sessions, IAppDatabase db, GitOperations operations, MachineForward? forward, RemoteConnections? connections, IGit git, IProjectRoots? projects = null) : IFeature
{
    private readonly FolderViews views = new(db, projects);

    public void Register(ICallRegistry calls)
    {
        calls = Route(calls);
        calls.Read("git", "checkouts", SourceControlJson.Default.Args, SourceControlJson.Default.Checkouts,
            (args, call) => ReadCheckoutsAsync(args.Session, call));
        calls.Read("git", "status", SourceControlJson.Default.Args, SourceControlJson.Default.GitStatus, (args, call) =>
            ReadCheckoutAsync(args.Session, args.CheckoutPath, args.ExpectedCheckoutRoot, call,
                root => new GitStatusReader(git).ReadAsync(root, call.Cancellation)));
        calls.Read("git", "inUse", SourceControlJson.Default.Args, SourceControlJson.Default.FolderInUse, (args, call) =>
            Task.FromResult(FolderView.IsView(args.Session)
                ? new FolderInUse(views.WorkingIn(FolderView.Parse(args.Session).LocalFolder(), call.Cancellation))
                : throw new CallBadArguments("Only a viewed folder is asked who works in it.")));
        calls.Read("git", "diff", SourceControlJson.Default.DiffArgs, SourceControlJson.Default.GitFileDiff, (args, call) =>
            ReadCheckoutAsync(args.Session, args.CheckoutPath, args.ExpectedCheckoutRoot, call, async root =>
        {
            var path = Path.GetRelativePath(root, SessionPaths.Resolve(root, args.Path));
            if (path == ".")
                throw new CallFailed("expected a file in the session's folder");
            var originalPath = args.OriginalPath is null ? null : Path.GetRelativePath(root, SessionPaths.Resolve(root, args.OriginalPath));
            return await new GitDiffReader(git).ReadAsync(root, path, originalPath, call.Cancellation);
        }));
        calls.Read("git", "branchDiff", SourceControlJson.Default.Args, SourceControlJson.Default.GitBranchDiff, (args, call) =>
            ReadCheckoutAsync(args.Session, args.CheckoutPath, args.ExpectedCheckoutRoot, call,
                root => new GitBranchDiffReader(git).ReadAsync(root, call.Cancellation)));
        calls.Read("git", "branches", SourceControlJson.Default.Args, SourceControlJson.Default.GitBranchList, async (args, call) =>
            await new GitBranches(git).ListAsync(SessionPaths.RootOf(sessions, args.Session), call.Cancellation));
        calls.Read("git", "stashes", SourceControlJson.Default.Args, SourceControlJson.Default.IReadOnlyListGitStash, async (args, call) =>
            await new GitStashes(git, TimeProvider.System).ListAsync(SessionPaths.RootOf(sessions, args.Session), call.Cancellation));
        calls.Read("git", "history", SourceControlJson.Default.HistoryArgs, SourceControlJson.Default.GitHistoryPage, (args, call) =>
            ReadCheckoutAsync(args.Session, args.CheckoutPath, args.ExpectedCheckoutRoot, call, async root =>
        {
            var path = args.Path is null ? null : FilePath(root, args.Path);
            return await new GitHistoryReader(git).ReadAsync(root, args.Cursor, path, call.Cancellation);
        }));
        calls.Read("git", "commitFiles", SourceControlJson.Default.CommitArgs, SourceControlJson.Default.IReadOnlyListGitCommitFile, (args, call) =>
            ReadCheckoutAsync(args.Session, args.CheckoutPath, args.ExpectedCheckoutRoot, call,
                root => new GitCommitReader(git).FilesAsync(root, args.Sha, call.Cancellation)));
        calls.Read("git", "commitDiff", SourceControlJson.Default.CommitDiffArgs, SourceControlJson.Default.GitFileDiff, (args, call) =>
            ReadCheckoutAsync(args.Session, args.CheckoutPath, args.ExpectedCheckoutRoot, call,
                root => new GitCommitReader(git).DiffAsync(root, args.Sha, FilePath(root, args.Path), call.Cancellation)));
    }

    private static string FilePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, SessionPaths.Resolve(root, path));
        return relative == "." ? throw new CallFailed("expected a file in the session's folder") : relative;
    }

    internal sealed record HistoryArgs([property: JsonRequired] string Session, string? Cursor, string? Path, ExpectedSource? ExpectedSource = null, string? CheckoutPath = null, string? ExpectedCheckoutRoot = null);
    internal sealed record CommitArgs([property: JsonRequired] string Session, [property: JsonRequired] string Sha, ExpectedSource? ExpectedSource = null, string? CheckoutPath = null, string? ExpectedCheckoutRoot = null);
    internal sealed record CommitDiffArgs([property: JsonRequired] string Session, [property: JsonRequired] string Sha, [property: JsonRequired] string Path, ExpectedSource? ExpectedSource = null, string? CheckoutPath = null, string? ExpectedCheckoutRoot = null);
    internal sealed record DiffArgs([property: JsonRequired] string Session, [property: JsonRequired] string Path, string? OriginalPath, ExpectedSource? ExpectedSource = null, string? CheckoutPath = null, string? ExpectedCheckoutRoot = null);
    internal sealed record Args([property: JsonRequired] string Session, ExpectedSource? ExpectedSource = null, string? CheckoutPath = null, string? ExpectedCheckoutRoot = null);
    /// <summary>The title of the session working in a viewed folder, judged as a write would be, or null.</summary>
    internal sealed record FolderInUse(string? Session);
}

/// <summary>The arguments and answers for the session's git status, file diff and branch diff reads.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SourceControlFeature.Checkouts))]
[JsonSerializable(typeof(SourceControlFeature.Args))]
[JsonSerializable(typeof(GitStatus))]
[JsonSerializable(typeof(SourceControlFeature.FolderInUse))]
[JsonSerializable(typeof(SourceControlFeature.DiffArgs))]
[JsonSerializable(typeof(GitFileDiff))]
[JsonSerializable(typeof(GitBranchDiff))]
[JsonSerializable(typeof(SourceControlFeature.HistoryArgs))]
[JsonSerializable(typeof(SourceControlFeature.CommitArgs))]
[JsonSerializable(typeof(SourceControlFeature.CommitDiffArgs))]
[JsonSerializable(typeof(GitHistoryPage))]
[JsonSerializable(typeof(GitBranchList))]
[JsonSerializable(typeof(IReadOnlyList<GitStash>))]
[JsonSerializable(typeof(IReadOnlyList<GitCommitFile>))]
internal sealed partial class SourceControlJson : JsonSerializerContext;
