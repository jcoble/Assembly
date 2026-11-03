using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// A session's turns over the socket, on whichever machine runs its agent: <c>send/send</c> queues an owner prompt,
/// <c>send/stop</c> stops a run, <c>send/answer</c> answers an ask the agent opened, and
/// session close stops background work. One the machine turns down (an ask no longer open, a task that can't
/// be stopped) is <c>failed</c>.
/// </summary>
internal sealed class SendFeature : IFeature
{
    public const string Feature = "send";

    private readonly SessionStore _store;
    private readonly MachineSet _machines;
    private readonly LiveHub _hub;
    private readonly IEngineDoor _door;

    public SendFeature(SessionStore store, MachineSet machines, LiveHub hub, IEngineDoor door)
    {
        _store = store;
        _machines = machines;
        _hub = hub;
        _door = door;
    }

    // Each is a write keyed on its session (a stop, on its run's), so one session's calls reach its machine in the order
    // sent, a machine that hangs holds up only its sessions' calls, and a closing socket doesn't cancel one already
    // queued. Machine I/O and answer preparation run outside the write; its returned closure applies the answer.
    public void Register(ICallRegistry calls)
    {
        calls.Write(Feature, "send", SessionsJson.Default.SendArgs, SessionsJson.Default.OwnerPromptAccepted, async (args, call) =>
        {
            var session = Session(args.Session);
            // Native subagents take prompts only from their parent; started sessions also accept the person's prompts.
            if (!session.Helper && session.ParentSessionId is { } parent && _store.Get(parent) is not null)
                throw new CallFailed("a subagent's session takes prompts from its parent only");
            var request = OwnerRequest(args);
            return await _door.PrepareOwnerPromptAsync(request, call.Cancellation);
        }, args => OrderOf(args.Session));
        calls.Write(Feature, "stop", SessionsJson.Default.StopArgs, SessionsJson.Default.Done, async (args, call) =>
        {
            var stopped = _hub.SessionOfRun(args.RunId) is { } sessionId && _store.Get(sessionId) is { } session
                && await _machines.For(session).StopAsync(session, args.RunId, call.Cancellation);
            return stopped ? _ => new Done(true) : throw new CallFailed("that run isn't running");
        }, args => _hub.SessionOfRun(args.RunId) is { } session ? OrderOf(session) : null);
        calls.Write(Feature, "answer", SessionsJson.Default.AnswerArgs, SessionsJson.Default.Done, async (args, call) =>
        {
            if (args.Answer.ValueKind != JsonValueKind.Object)
                throw new CallFailed("expected { optionId } or { action, content }");
            var session = Session(args.Session);
            var answer = await _door.PrepareNeedAnswerAsync(new(session.SessionId, args.Ask, args.Answer), call.Cancellation);
            return scope => answer(scope)
                ? new Done(true)
                : throw new CallFailed("that ask isn't open, or the answer doesn't fit it");
        }, args => OrderOf(args.Session));
        calls.Write(Feature, "stopTask", SessionsJson.Default.StopTaskArgs, SessionsJson.Default.Done, async (args, call) =>
        {
            var session = Session(args.Session);
            return await _machines.For(session).StopTaskAsync(session, args.Task, call.Cancellation)
                ? _ => new Done(true)
                : throw new CallFailed("that task isn't running, or can't be stopped");
        }, args => OrderOf(args.Session));
    }

    /// <summary>The order key of the writes that reach a session's machine.</summary>
    internal static string OrderOf(string sessionId) => $"session {sessionId}";

    private SessionInfo Session(string id) => _store.Get(id) ?? throw new CallFailed("unknown session");

    /// <summary>Why <paramref name="clientId"/> can't name a send, or null if it can.</summary>
    internal static string? ClientIdError(string? clientId) =>
        clientId is { Length: >= 1 and <= 64 } && clientId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? null
            : "clientId must be 1 to 64 letters, digits, - or _";

    /// <summary><c>ClientId</c>, when given, replays the original committed owner-prompt acknowledgement.</summary>
    internal sealed record SendArgs([property: JsonRequired] string Session, [property: JsonRequired] string Text, string? ClientId = null,
        IReadOnlyList<string>? AttachmentIds = null, long? Epoch = null);

    internal static OwnerPromptRequest OwnerRequest(SendArgs args)
    {
        if (args.Epoch < 0) throw new CallFailed("epoch must be nonnegative");
        if (args.ClientId is not null && ClientIdError(args.ClientId) is { } error) throw new CallFailed(error);
        return new(args.Session, args.Text, args.ClientId ?? Guid.NewGuid().ToString("N"),
            args.AttachmentIds?.ToArray() ?? [], args.Epoch);
    }

    internal sealed record StopArgs([property: JsonRequired] string RunId);

    internal sealed record AnswerArgs([property: JsonRequired] string Session, [property: JsonRequired] string Ask,
        [property: JsonRequired] JsonElement Answer);

    internal sealed record StopTaskArgs([property: JsonRequired] string Session, [property: JsonRequired] string Task);

    internal sealed record Done(bool Ok);
}
