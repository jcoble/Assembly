using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Engine.Messages.Reading;

namespace Assembly.Infrastructure.Engine.Messages;

public sealed record MessagesPageArgs(MessageSessionKey? Session = null, string? RunMachine = null, string? RunId = null,
    string? Status = null, string Search = "", string Sort = "time", string? After = null, int Limit = 100, string? Machine = null);
public sealed record MessagesCountArgs(MessageSessionKey Session, IReadOnlyList<SeenCut> SeenCuts, string? Machine = null);

/// <summary>The sole owner-facing traffic reader and fixed bulk authority reads; it never submits commands.</summary>
internal sealed class MessagesCalls(EngineRuntime runtime, IAppDatabase db, MachineForward? forward = null) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        var query = new MessageQueries(db, runtime.Machine); var sources = new MessageReadSources(query, runtime.Machine, forward);
        calls.Read("messages", "page", MessageReadJson.Default.MessagesPageArgs, MessageReadJson.Default.MessagePage, (args, call) => Read(call, async () =>
        {
            var scope = new MessageScope(args.Machine, args.Session?.Machine, args.Session?.Id, args.RunMachine, args.RunId, args.Status, args.Search, args.Sort);
            return await sources.PageAsync(scope, args.After is null ? null : MessageQueries.DecodeCursor(args.After), args.Limit, call.Cancellation).ConfigureAwait(false);
        }));
        calls.Read("messages", "count", MessageReadJson.Default.MessagesCountArgs, MessageReadJson.Default.MessageCount,
            (args, call) => Read(call, () => sources.CountAsync(args.Session, args.SeenCuts, args.Machine, call.Cancellation)));
        calls.Read("messages", "body", MessageReadJson.Default.BodyArgs, MessageReadJson.Default.JsonElement,
            (args, call) => Read(call, () => sources.BodyAsync(args, call.Cancellation)), bulk: true);
        calls.Read("messages", "match", MessageReadJson.Default.MatchArgs, MessageReadJson.Default.BodyMatch,
            (args, call) => Read(call, () => Task.FromResult(query.Match(args, call.Cancellation))), bulk: true);
    }

    private static async Task<T> Read<T>(CallContext call, Func<Task<T>> read)
    {
        if (call.Peer is not OwnerPeer) throw new CallFailed("Only the authenticated owner or machine connection can read message traffic.", "forbidden");
        call.Cancellation.ThrowIfCancellationRequested();
        try { var result = await read().ConfigureAwait(false); call.Cancellation.ThrowIfCancellationRequested(); return result; }
        catch (ArgumentException error) { throw new CallBadArguments(error.Message); }
        catch (InvalidOperationException error) when (error.Message is "query_changed" or "message_body_too_large" or "message_response_too_large" or "message_candidate_limit")
        { throw new CallFailed(error.Message, error.Message); }
    }
}
