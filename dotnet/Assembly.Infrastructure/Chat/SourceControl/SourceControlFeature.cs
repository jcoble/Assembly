using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.SourceControl;
using Assembly.Infrastructure.Git;
using Assembly.Application.Common.Database;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Chat.SourceControl;

/// <summary>Reads a session's git status, file diffs and branch diff over the socket from its local folder.</summary>
internal sealed partial class SourceControlFeature(SessionStore sessions, IAppDatabase db, GitOperations operations, MachineForward? forward, RemoteConnections? connections) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        calls = Route(calls);
        calls.Read("git", "status", SourceControlJson.Default.Args, SourceControlJson.Default.GitStatus, async (args, call) =>
            await new GitStatusReader(new GitCli()).ReadAsync(SessionPaths.RootOf(sessions, args.Session), call.Cancellation));
        calls.Read("git", "diff", SourceControlJson.Default.DiffArgs, SourceControlJson.Default.GitFileDiff, async (args, call) =>
        {
            var root = SessionPaths.RootOf(sessions, args.Session);
            var path = Path.GetRelativePath(root, SessionPaths.Resolve(root, args.Path));
            if (path == ".")
                throw new CallFailed("expected a file in the session's folder");
            var originalPath = args.OriginalPath is null ? null : Path.GetRelativePath(root, SessionPaths.Resolve(root, args.OriginalPath));
            return await new GitDiffReader(new GitCli()).ReadAsync(root, path, originalPath, call.Cancellation);
        });
        calls.Read("git", "branchDiff", SourceControlJson.Default.Args, SourceControlJson.Default.GitBranchDiff, async (args, call) =>
            await new GitBranchDiffReader(new GitCli()).ReadAsync(SessionPaths.RootOf(sessions, args.Session), call.Cancellation));
        calls.Read("git", "branches", SourceControlJson.Default.Args, SourceControlJson.Default.GitBranchList, async (args, call) =>
            await new GitBranches(new GitCli()).ListAsync(SessionPaths.RootOf(sessions, args.Session), call.Cancellation));
        calls.Read("git", "stashes", SourceControlJson.Default.Args, SourceControlJson.Default.IReadOnlyListGitStash, async (args, call) =>
            await new GitStashes(new GitCli(), TimeProvider.System).ListAsync(SessionPaths.RootOf(sessions, args.Session), call.Cancellation));
        calls.Read("git", "history", SourceControlJson.Default.HistoryArgs, SourceControlJson.Default.GitHistoryPage, async (args, call) =>
        {
            var root = SessionPaths.RootOf(sessions, args.Session);
            var path = args.Path is null ? null : FilePath(root, args.Path);
            return await new GitHistoryReader(new GitCli()).ReadAsync(root, args.Cursor, path, call.Cancellation);
        });
        calls.Read("git", "commitFiles", SourceControlJson.Default.CommitArgs, SourceControlJson.Default.IReadOnlyListGitCommitFile, async (args, call) =>
            await new GitCommitReader(new GitCli()).FilesAsync(SessionPaths.RootOf(sessions, args.Session), args.Sha, call.Cancellation));
        calls.Read("git", "commitDiff", SourceControlJson.Default.CommitDiffArgs, SourceControlJson.Default.GitFileDiff, async (args, call) =>
        {
            var root = SessionPaths.RootOf(sessions, args.Session);
            return await new GitCommitReader(new GitCli()).DiffAsync(root, args.Sha, FilePath(root, args.Path), call.Cancellation);
        });
    }

    private static string FilePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, SessionPaths.Resolve(root, path));
        return relative == "." ? throw new CallFailed("expected a file in the session's folder") : relative;
    }

    internal sealed record HistoryArgs([property: JsonRequired] string Session, string? Cursor, string? Path, ExpectedSource? ExpectedSource = null);
    internal sealed record CommitArgs([property: JsonRequired] string Session, [property: JsonRequired] string Sha, ExpectedSource? ExpectedSource = null);
    internal sealed record CommitDiffArgs([property: JsonRequired] string Session, [property: JsonRequired] string Sha, [property: JsonRequired] string Path, ExpectedSource? ExpectedSource = null);
    internal sealed record DiffArgs([property: JsonRequired] string Session, [property: JsonRequired] string Path, string? OriginalPath, ExpectedSource? ExpectedSource = null);
    internal sealed record Args([property: JsonRequired] string Session, ExpectedSource? ExpectedSource = null);
}

/// <summary>The arguments and answers for the session's git status, file diff and branch diff reads.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SourceControlFeature.Args))]
[JsonSerializable(typeof(GitStatus))]
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
