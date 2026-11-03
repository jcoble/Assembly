using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.SourceControl.PullRequests;
namespace Assembly.Infrastructure.Chat.SourceControl;

/// <summary>Fixed read kinds; the owning machine supplies GitHub identity and immutable versions.</summary>
internal sealed class PullRequestsFeature(SessionStore sessions, PullRequestReader reader, SourceControlFeature routes) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        calls = routes.Route(calls);
        calls.Read("git", "prs", PullRequestCallsJson.Default.ListArgs, PullRequestCallsJson.Default.PullRequestPage, async (args, call) =>
        {
            Owner(call);
            if (args.Session is not null)
                return await reader.ListForRootAsync(SessionPaths.RootOf(sessions, args.Session), args.State, args.Search, args.Cursor, call.Cancellation);
            return await reader.ListAsync(args.Repositories ?? throw new CallBadArguments("Choose project repositories."), args.State, args.Search, args.Cursor, call.Cancellation);
        });
        calls.Read("git", "pr", PullRequestCallsJson.Default.DetailArgs, PullRequestCallsJson.Default.PullRequestDetail, (args, call) =>
        {
            Owner(call);
            return reader.DetailAsync(SessionPaths.RootOf(sessions, args.Session), args.Number, call.Cancellation);
        }, bulk: true);
        calls.Read("git", "prFile", PullRequestCallsJson.Default.FileArgs, PullRequestCallsJson.Default.PrFileContext, (args, call) =>
        {
            Owner(call);
            return reader.FileAsync(SessionPaths.RootOf(sessions, args.Session), args.Number, args.Path, args.BaseSha, args.HeadSha, call.Cancellation);
        }, bulk: true);
    }
    private static void Owner(CallContext call) { if (call.Peer is not OwnerPeer) throw new CallFailed("forbidden"); }
    internal sealed record ListArgs(string? Session, IReadOnlyList<string>? Repositories, [property: JsonRequired] string State, string? Search, string? Cursor, ExpectedSource? ExpectedSource = null);
    internal sealed record DetailArgs([property: JsonRequired] string Session, [property: JsonRequired] int Number, ExpectedSource? ExpectedSource = null);
    internal sealed record FileArgs([property: JsonRequired] string Session, [property: JsonRequired] int Number, [property: JsonRequired] string Path, [property: JsonRequired] string BaseSha, [property: JsonRequired] string HeadSha, ExpectedSource? ExpectedSource = null);
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PullRequestsFeature.ListArgs))]
[JsonSerializable(typeof(PullRequestsFeature.DetailArgs))]
[JsonSerializable(typeof(PullRequestsFeature.FileArgs))]
[JsonSerializable(typeof(PullRequestPage))]
[JsonSerializable(typeof(PullRequestDetail))]
[JsonSerializable(typeof(PrFileContext))]
internal sealed partial class PullRequestCallsJson : JsonSerializerContext;
