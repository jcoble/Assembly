using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
using Assembly.Application.Common.GitHub;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.SourceControl;
using Assembly.Application.Features.Chat.SourceControl.PullRequests;
using Assembly.Infrastructure.Git;

namespace Assembly.Infrastructure.Chat.SourceControl;

/// <summary>Fixed session producers run inside the existing host-owned Git operation and checkout lease.</summary>
internal sealed class GitSessionOperations(SessionStore sessions, IGit git, PullRequestWriter prs, CheckoutWrites writes, GitOperations operations)
{
    internal static bool Supports(string? action) => action is "fetch" or "pull" or "push" or "publish" or "createPr" or "review" or "reply" or "comment" or "merge";
    internal void RequireSession(string session)
    {
        var actual = sessions.Get(session) ?? throw new CallFailed("unknown session");
        if (!string.IsNullOrEmpty(actual.Machine)) throw new CallFailed("This session is owned by another machine.");
    }
    internal Func<CancellationToken, Task<JsonElement>> Prepare(GitOperationStart request)
    {
        if (!Supports(request.Action) || request.Session is not { } session) throw new CallFailed("forbidden");
        RequireSession(session);
        var root = SessionPaths.RootOf(sessions, session);
        var machine = operations.Machine;
        var sync = new GitSync(git);
        Func<string, CancellationToken, Task<JsonElement>> execute = request.Action switch
        {
            "fetch" => async (path, ct) => JsonSerializer.SerializeToElement(await sync.FetchAsync(path, ct), GitWritesJson.Default.GitWriteResult),
            "pull" => Bind(request.Args, GitSessionOperationsJson.Default.SyncArgs, GitWritesJson.Default.GitWriteResult, (a, path, ct) => sync.PullAsync(path, Branch(a), ct)),
            "push" => Bind(request.Args, GitSessionOperationsJson.Default.SyncArgs, GitWritesJson.Default.GitWriteResult, (a, path, ct) => sync.PushAsync(path, Branch(a), ct)),
            "publish" => Bind(request.Args, GitSessionOperationsJson.Default.SyncArgs, GitWritesJson.Default.GitWriteResult, (a, path, ct) => sync.PublishAsync(path, Branch(a), a.Remote ?? throw new CallBadArguments("Choose a remote."), ct)),
            "createPr" => Bind(request.Args, GitSessionOperationsJson.Default.CreateArgs, GitSessionOperationsJson.Default.PullRequestCreated, (a, path, ct) => prs.CreateAsync(path, a.Base, a.Title, a.Description, a.Draft, ct)),
            "review" => Bind(request.Args, GitSessionOperationsJson.Default.ReviewArgs, GitSessionOperationsJson.Default.String, (a, path, ct) => prs.ReviewAsync(path, a.Number, a.ExpectedHead, a.Body, a.Comments, ct)),
            "reply" => Bind(request.Args, GitSessionOperationsJson.Default.ReplyArgs, GitSessionOperationsJson.Default.String, (a, path, ct) => prs.ReplyAsync(path, a.Number, a.ExpectedHead, a.CommentId, a.Body, ct)),
            "comment" => Bind(request.Args, GitSessionOperationsJson.Default.CommentArgs, GitSessionOperationsJson.Default.String, (a, path, ct) => prs.CommentAsync(path, a.Number, a.ExpectedHead, a.Body, ct)),
            "merge" => Bind(request.Args, GitSessionOperationsJson.Default.MergeArgs, GitSessionOperationsJson.Default.String, (a, path, ct) => prs.MergeAsync(path, a.Number, a.Method, a.ExpectedHead, a.ExpectedBase, ct)),
            _ => throw new CallFailed("forbidden")
        };
        return ct => writes.RunAsync(root, async () =>
        {
            ct.ThrowIfCancellationRequested();
            if (SessionPaths.RootOf(sessions, session) != root) throw new CallFailed("The session's checkout changed. Refresh Source Control and try again.");
            try { return await execute(root, ct); }
            catch (Exception error) when (error is GhInterrupted or GhUnconfirmed)
            { throw new CallUncertain(machine, "This may have posted. Refresh before trying again."); }
        });
    }
    private static string Branch(SyncArgs args) => args.ExpectedBranch ?? throw new CallBadArguments("Refresh the current branch before continuing.");
    private static Func<string, CancellationToken, Task<JsonElement>> Bind<TArgs, TResult>(JsonElement json, JsonTypeInfo<TArgs> input, JsonTypeInfo<TResult> output,
        Func<TArgs, string, CancellationToken, Task<TResult>> execute) where TArgs : class
    {
        TArgs args;
        try { args = json.Deserialize(input) ?? throw new CallBadArguments("Git action arguments are required."); }
        catch (JsonException) { throw new CallBadArguments("Invalid Git action arguments."); }
        return async (root, ct) => JsonSerializer.SerializeToElement(await execute(args, root, ct), output);
    }
    internal sealed record SyncArgs(string? ExpectedBranch, string? Remote);
    internal sealed record CreateArgs([property: JsonRequired] string Base, [property: JsonRequired] string Title,
        [property: JsonRequired] string Description, bool Draft);
    internal sealed record ReviewArgs(int Number, [property: JsonRequired] string ExpectedHead, [property: JsonRequired] string Body,
        [property: JsonRequired] IReadOnlyList<LineComment> Comments);
    internal sealed record ReplyArgs(int Number, [property: JsonRequired] string ExpectedHead, long CommentId, [property: JsonRequired] string Body);
    internal sealed record CommentArgs(int Number, [property: JsonRequired] string ExpectedHead, [property: JsonRequired] string Body);
    internal sealed record MergeArgs(int Number, [property: JsonRequired] string Method, [property: JsonRequired] string ExpectedHead, [property: JsonRequired] string ExpectedBase);
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GitSessionOperations.SyncArgs))]
[JsonSerializable(typeof(GitSessionOperations.CreateArgs))]
[JsonSerializable(typeof(GitSessionOperations.ReviewArgs))]
[JsonSerializable(typeof(GitSessionOperations.ReplyArgs))]
[JsonSerializable(typeof(GitSessionOperations.CommentArgs))]
[JsonSerializable(typeof(GitSessionOperations.MergeArgs))]
[JsonSerializable(typeof(PullRequestCreated))]
[JsonSerializable(typeof(string))]
internal sealed partial class GitSessionOperationsJson : JsonSerializerContext;
