using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Git;
using Assembly.Application.Common.GitHub;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.SourceControl;
using Assembly.Application.Features.Chat.SourceControl.PullRequests;
using Assembly.Infrastructure.Git;

namespace Assembly.Infrastructure.Chat.SourceControl;

/// <summary>Fixed session producers run inside the existing host-owned Git operation and checkout lease.</summary>
internal sealed class GitSessionOperations(SessionStore sessions, IGit git, PullRequestWriter prs, CheckoutWrites writes, GitOperations operations, GitDrafts drafts, PullRequestReader reader, IAppDatabase db)
{
    internal static bool Supports(string? action) => action is "fetch" or "pull" or "push" or "publish" or "createPr" or "review" or "reply" or "comment" or "merge" or "draftCommitMessage" or "draftPr" or "draftReview";
    internal void RequireSession(string session)
    {
        if (FolderView.IsView(session)) return;
        var actual = sessions.Get(session) ?? throw new CallFailed("unknown session");
        if (!string.IsNullOrEmpty(actual.Machine)) throw new CallFailed("This session is owned by another machine.");
    }
    internal Func<CancellationToken, Task<JsonElement>> Prepare(GitOperationStart request)
    {
        if (!Supports(request.Action) || request.Session is not { } session) throw new CallFailed("forbidden");
        RequireSession(session);
        SourceControlFeature.RequireLocalOwner(db, operations.Machine, session);
        if (request.ExpectedSource is { } source && (source.Machine != operations.Machine || source.Generation != 0 || source.RouteMachine is not null))
            throw new CallFailed("The session's owning machine changed.", "source_changed");
        var root = SourceControlWrites.RequireRoot(sessions, session, request.ExpectedCheckoutRoot, request.ExpectedSource is not null);
        var machine = operations.Machine;
        var execute = BindAction(request);
        return ct => writes.RunAsync(root, async () =>
        {
            ct.ThrowIfCancellationRequested();
            SourceControlFeature.RequireLocalOwner(db, machine, session);
            if (SourceControlWrites.RequireRoot(sessions, session, request.ExpectedCheckoutRoot, request.ExpectedSource is not null) != root)
                throw new CallFailed("The session's checkout changed. Refresh and try again.", "source_changed");
            if (FolderView.IsView(session)) new FolderViews(db, null).RequireWritable(root, ct);
            try { return await execute(root, ct); }
            catch (Exception error) when (error is GhInterrupted or GhUnconfirmed)
            { throw new CallUncertain(machine, "This may have posted. Refresh before trying again."); }
        });
    }
    internal Func<CancellationToken, Task<JsonElement>> PrepareProject(GitOperationStart request, string root, string repository, Action ensureCurrent)
    {
        if (!GitOperations.ProjectPrAction(request.Action) || request.Session is not null || string.IsNullOrWhiteSpace(request.ProjectId) ||
            request.ExpectedCheckoutRoot != root || !string.Equals(request.ExpectedRepository, repository, StringComparison.OrdinalIgnoreCase))
            throw new CallFailed("The project repository changed. Refresh and try again.", "source_changed");
        var execute = BindAction(request, ensureCurrent);
        return ct => writes.RunAsync(root, async () =>
        {
            ct.ThrowIfCancellationRequested();
            ensureCurrent();
            try { return await execute(root, ct); }
            catch (Exception error) when (error is GhInterrupted or GhUnconfirmed)
            { throw new CallUncertain(operations.Machine, "This may have posted. Refresh before trying again."); }
        });
    }

    private Func<string, CancellationToken, Task<JsonElement>> BindAction(GitOperationStart request, Action? ensureCurrent = null)
    {
        var sync = new GitSync(git);
        return request.Action switch
        {
            "fetch" => async (path, ct) => JsonSerializer.SerializeToElement(await sync.FetchAsync(path, ct), GitWritesJson.Default.GitWriteResult),
            "pull" => Bind(request.Args, GitSessionOperationsJson.Default.SyncArgs, GitWritesJson.Default.GitWriteResult, (a, path, ct) => sync.PullAsync(path, Branch(a), ct)),
            "push" => Bind(request.Args, GitSessionOperationsJson.Default.SyncArgs, GitWritesJson.Default.GitWriteResult, (a, path, ct) => sync.PushAsync(path, Branch(a), ct)),
            "publish" => Bind(request.Args, GitSessionOperationsJson.Default.SyncArgs, GitWritesJson.Default.GitWriteResult, (a, path, ct) => sync.PublishAsync(path, Branch(a), a.Remote ?? throw new CallBadArguments("Choose a remote."), ct)),
            "createPr" => Bind(request.Args, GitSessionOperationsJson.Default.CreateArgs, GitSessionOperationsJson.Default.PullRequestCreated, (a, path, ct) => prs.CreateAsync(path, a.Base, a.Title, a.Description, a.Draft, ct)),
            "review" => Bind(request.Args, GitSessionOperationsJson.Default.ReviewArgs, GitSessionOperationsJson.Default.String, (a, path, ct) => prs.ReviewAsync(path, a.Number, a.ExpectedHead, a.Body, a.Comments, ct, request.ProjectId is null ? null : request.ExpectedRepository, ensureCurrent)),
            "reply" => Bind(request.Args, GitSessionOperationsJson.Default.ReplyArgs, GitSessionOperationsJson.Default.String, (a, path, ct) => prs.ReplyAsync(path, a.Number, a.ExpectedHead, a.CommentId, a.Body, ct, request.ProjectId is null ? null : request.ExpectedRepository, ensureCurrent)),
            "comment" => Bind(request.Args, GitSessionOperationsJson.Default.CommentArgs, GitSessionOperationsJson.Default.String, (a, path, ct) => prs.CommentAsync(path, a.Number, a.ExpectedHead, a.Body, ct, request.ProjectId is null ? null : request.ExpectedRepository, ensureCurrent)),
            "merge" => Bind(request.Args, GitSessionOperationsJson.Default.MergeArgs, GitSessionOperationsJson.Default.String, (a, path, ct) => prs.MergeAsync(path, a.Number, a.Method, a.ExpectedHead, a.ExpectedBase, ct, request.ProjectId is null ? null : request.ExpectedRepository, ensureCurrent)),
            "draftCommitMessage" => async (path, ct) => JsonSerializer.SerializeToElement(await drafts.CommitMessageAsync(path, ct), GitSessionOperationsJson.Default.String),
            "draftPr" => Bind(request.Args, GitSessionOperationsJson.Default.DraftPrArgs, GitSessionOperationsJson.Default.PullRequestDraft, async (a, path, ct) =>
            {
                var draft = await drafts.PullRequestAsync(path, a.Base, ct);
                return new PullRequestDraft(draft.Title, draft.Description);
            }),
            "draftReview" => Bind(request.Args, GitSessionOperationsJson.Default.DraftReviewArgs, GitSessionOperationsJson.Default.ReviewDraft, async (a, path, ct) =>
            {
                var detail = request.ProjectId is null ? await reader.DetailAsync(path, a.Number, ct) :
                    await reader.ProjectDetailAsync(path, request.ExpectedRepository!, a.Number, ct);
                if (detail.HeadSha != a.ExpectedHead) throw new CallFailed("The PR head changed. Refresh and try again.");
                ensureCurrent?.Invoke();
                return await drafts.ReviewAsync(detail, ct);
            }),
            _ => throw new CallFailed("forbidden")
        };
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
    internal sealed record DraftPrArgs([property: JsonRequired] string Base);
    internal sealed record DraftReviewArgs(int Number, [property: JsonRequired] string ExpectedHead);
    internal sealed record PullRequestDraft(string Title, string Description);
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
[JsonSerializable(typeof(GitSessionOperations.DraftPrArgs))]
[JsonSerializable(typeof(GitSessionOperations.DraftReviewArgs))]
[JsonSerializable(typeof(GitSessionOperations.PullRequestDraft))]
[JsonSerializable(typeof(ReviewDraft))]
internal sealed partial class GitSessionOperationsJson : JsonSerializerContext;
