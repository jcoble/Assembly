using Assembly.Application.Common.Calls;
namespace Assembly.Infrastructure.Chat.SourceControl;

/// <summary>The reviewed fixed Git socket surface; network mutations are start actions.</summary>
internal static class GitCallKinds
{
    internal static CallKind? KindOf(string method) => method switch
    {
        "status" or "diff" or "branchDiff" or "branches" or "stashes" or "history" or "commitFiles" or "commitDiff" or "prs" or "operation_host" or "operation" => CallKind.Read,
        "pr" or "prFile" or "project_pr" or "project_prFile" => CallKind.BulkRead,
        "stage" or "unstage" or "discard" or "commit" or "createBranch" or "stash" or "applyStash" or "dropStash" or "addRemote" or "init" or "start" or "cancel" or "release" => CallKind.Write,
        _ => null
    };
}
