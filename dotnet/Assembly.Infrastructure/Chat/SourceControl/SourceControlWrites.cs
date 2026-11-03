using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Git;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.SourceControl;
using Assembly.Infrastructure.Git;

namespace Assembly.Infrastructure.Chat.SourceControl;
/// <summary>Short owner writes use the same host checkout lease as Engine checkout effects.</summary>
internal sealed class SourceControlWrites(SessionStore sessions, IGit cli, CheckoutWrites writes, TimeProvider time, SourceControlFeature routes) : IFeature
{
    private readonly GitWriter git = new(cli);
    public void Register(ICallRegistry calls)
    {
        calls = routes.Route(calls);
        calls.Write("git", "stage", GitWritesJson.Default.PathsArgs, GitWritesJson.Default.GitWriteResult,
            (args, call) => Write(args.Session, call, root => args.Paths is null
                ? git.StageAllAsync(root, RequireFingerprint(args.ExpectedFingerprint), call.Cancellation)
                : git.StageAsync(root, Paths(root, args.Paths), call.Cancellation)), args => args.Session);
        calls.Write("git", "unstage", GitWritesJson.Default.PathsArgs, GitWritesJson.Default.GitWriteResult,
            (args, call) => Write(args.Session, call, root => args.Paths is null
                ? git.UnstageAllAsync(root, RequireFingerprint(args.ExpectedFingerprint), call.Cancellation)
                : git.UnstageAsync(root, Paths(root, args.Paths), call.Cancellation)), args => args.Session);
        calls.Write("git", "discard", GitWritesJson.Default.DiscardArgs, GitWritesJson.Default.GitWriteResult,
            (args, call) => Write(args.Session, call, root =>
            {
                if (args.ExpectedFingerprint is not null)
                {
                    if (args.Tracked is not null || args.Untracked is not null || args.Expected is not null) throw new CallBadArguments("Choose listed files or all changes.");
                    return git.DiscardAllAsync(root, args.ExpectedFingerprint, args.IncludeUntracked, call.Cancellation);
                }
                if (args.Tracked is null || args.Untracked is null || args.Expected is null) throw new CallBadArguments("The reviewed files are required.");
                return git.DiscardAsync(root, Paths(root, args.Tracked), Paths(root, args.Untracked), args.Expected, call.Cancellation);
            }), args => args.Session);
        calls.Write("git", "commit", GitWritesJson.Default.CommitArgs, GitWritesJson.Default.GitWriteResult,
            (args, call) => Write(args.Session, call, root => git.CommitAsync(root, args.Message, args.Amend, args.ExpectedHead, call.Cancellation)), args => args.Session);
        calls.Write("git", "addRemote", GitWritesJson.Default.RemoteArgs, GitWritesJson.Default.GitWriteResult,
            (args, call) => Write(args.Session, call, root => git.AddRemoteAsync(root, args.Name, args.Url, call.Cancellation)), args => args.Session);
        calls.Write("git", "init", GitWritesJson.Default.SessionArgs, GitWritesJson.Default.GitWriteResult,
            (args, call) => Write(args.Session, call, root => git.InitAsync(root, call.Cancellation)), args => args.Session);
        calls.Write("git", "createBranch", GitWritesJson.Default.BranchArgs, GitWritesJson.Default.GitWriteResult,
            (args, call) => Write(args.Session, call, root => new GitBranches(cli).CreateAsync(root, args.Name, args.ExpectedHead, call.Cancellation)), args => args.Session);
        calls.Write("git", "stash", GitWritesJson.Default.StashArgs, GitWritesJson.Default.GitWriteResult,
            (args, call) => Write(args.Session, call, root => new GitStashes(cli, time).StashAsync(root, args.Session, args.Message, args.IncludeUntracked, call.Cancellation)), args => args.Session);
        calls.Write("git", "applyStash", GitWritesJson.Default.StashShaArgs, GitWritesJson.Default.GitWriteResult,
            (args, call) => Write(args.Session, call, root => new GitStashes(cli, time).ApplyAsync(root, args.Sha, call.Cancellation)), args => args.Session);
        calls.Write("git", "dropStash", GitWritesJson.Default.StashShaArgs, GitWritesJson.Default.GitWriteResult,
            (args, call) => Write(args.Session, call, root => new GitStashes(cli, time).DropAsync(root, args.Sha, call.Cancellation)), args => args.Session);
    }
    private async Task<Func<IWriteScope, GitWriteResult>> Write(string session, CallContext call, Func<string, Task<GitWriteResult>> execute)
    {
        if (call.Peer is not OwnerPeer) throw new CallFailed("forbidden");
        var root = SessionPaths.RootOf(sessions, session);
        var result = await writes.RunAsync(root, async () =>
        {
            call.Cancellation.ThrowIfCancellationRequested();
            if (SessionPaths.RootOf(sessions, session) != root) throw new CallFailed("The session's checkout changed. Refresh Source Control and try again.");
            return await execute(root);
        });
        return _ => result;
    }
    private static IReadOnlyList<string> Paths(string root, IReadOnlyList<string> paths) => paths.Select(path =>
    {
        var relative = Path.GetRelativePath(root, SessionPaths.Resolve(root, path));
        return relative == "." ? throw new CallBadArguments("Choose files to change.") : relative;
    }).Distinct(StringComparer.Ordinal).ToArray();
    private static string RequireFingerprint(string? fingerprint) => fingerprint ?? throw new CallBadArguments("Refresh Source Control before changing all files.");
    internal sealed record BranchArgs([property: JsonRequired] string Session, [property: JsonRequired] string Name, string? ExpectedHead);
    internal sealed record StashArgs([property: JsonRequired] string Session, [property: JsonRequired] string Message, bool IncludeUntracked);
    internal sealed record StashShaArgs([property: JsonRequired] string Session, [property: JsonRequired] string Sha);
    internal sealed record SessionArgs([property: JsonRequired] string Session);
    internal sealed record PathsArgs([property: JsonRequired] string Session, IReadOnlyList<string>? Paths, string? ExpectedFingerprint);
    internal sealed record DiscardArgs([property: JsonRequired] string Session, IReadOnlyList<string>? Tracked, IReadOnlyList<string>? Untracked, IReadOnlyList<ChangedFile>? Expected, string? ExpectedFingerprint, bool IncludeUntracked);
    internal sealed record CommitArgs([property: JsonRequired] string Session, [property: JsonRequired] string Message, bool Amend, string? ExpectedHead);
    internal sealed record RemoteArgs([property: JsonRequired] string Session, [property: JsonRequired] string Name, [property: JsonRequired] string Url);
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SourceControlWrites.SessionArgs))]
[JsonSerializable(typeof(SourceControlWrites.PathsArgs))]
[JsonSerializable(typeof(SourceControlWrites.DiscardArgs))]
[JsonSerializable(typeof(SourceControlWrites.CommitArgs))]
[JsonSerializable(typeof(SourceControlWrites.RemoteArgs))]
[JsonSerializable(typeof(SourceControlWrites.BranchArgs))]
[JsonSerializable(typeof(SourceControlWrites.StashArgs))]
[JsonSerializable(typeof(SourceControlWrites.StashShaArgs))]
[JsonSerializable(typeof(GitWriteResult))]
internal sealed partial class GitWritesJson : JsonSerializerContext;
