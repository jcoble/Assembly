using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.SourceControl;
using Assembly.Infrastructure.Git;

namespace Assembly.Infrastructure.Chat.SourceControl;

/// <summary>Reads a session's git status and file diffs over the socket from its local folder.</summary>
internal sealed class SourceControlFeature(SessionStore sessions) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        calls.Read("git", "status", SourceControlJson.Default.Args, SourceControlJson.Default.GitStatus, async (args, call) =>
            await new GitStatusReader(new GitCli()).ReadAsync(SessionPaths.RootOf(sessions, args.Session), call.Cancellation));
        calls.Read("git", "diff", SourceControlJson.Default.DiffArgs, SourceControlJson.Default.GitFileDiff, async (args, call) =>
        {
            var root = SessionPaths.RootOf(sessions, args.Session);
            var path = Path.GetRelativePath(root, SessionPaths.Resolve(root, args.Path));
            var originalPath = args.OriginalPath is null ? null : Path.GetRelativePath(root, SessionPaths.Resolve(root, args.OriginalPath));
            return await new GitDiffReader(new GitCli()).ReadAsync(root, path, originalPath, call.Cancellation);
        });
    }

    internal sealed record DiffArgs([property: JsonRequired] string Session, [property: JsonRequired] string Path, string? OriginalPath);
    internal sealed record Args([property: JsonRequired] string Session);
}

/// <summary>The arguments and answers for the session's git status and diff reads.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SourceControlFeature.Args))]
[JsonSerializable(typeof(GitStatus))]
[JsonSerializable(typeof(SourceControlFeature.DiffArgs))]
[JsonSerializable(typeof(GitFileDiff))]
internal sealed partial class SourceControlJson : JsonSerializerContext;
