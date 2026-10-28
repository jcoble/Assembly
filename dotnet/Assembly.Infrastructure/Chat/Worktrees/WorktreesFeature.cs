using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.Worktrees;
using Assembly.Infrastructure.Git;

namespace Assembly.Infrastructure.Chat.Worktrees;

/// <summary>Reads a session's worktrees over the socket from its local folder.</summary>
internal sealed class WorktreesFeature(SessionStore sessions) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        calls.Read("worktrees", "list", WorktreesJson.Default.Args, WorktreesJson.Default.WorktreeList, async (args, call) =>
            new WorktreeList((await new WorktreeReader(new GitCli()).ReadAsync(SessionPaths.RootOf(sessions, args.Session), call.Cancellation)).ToArray()));
    }

    internal sealed record Args([property: JsonRequired] string Session);

    internal sealed record WorktreeList(Worktree[] Worktrees);
}

/// <summary>The arguments and answer for the session's worktrees/list read.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorktreesFeature.Args))]
[JsonSerializable(typeof(WorktreesFeature.WorktreeList))]
internal sealed partial class WorktreesJson : JsonSerializerContext;
