using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// A session's turns over the socket (plan C1), on whichever machine runs its agent: <c>send/send</c> starts a turn or
/// steers the one running, <c>send/stop</c> stops a run, <c>send/answer</c> answers an ask the agent opened, and
/// <c>send/stopTask</c> stops background work. One the machine turns down (an ask no longer open, a task that can't
/// be stopped) is <c>failed</c>.
/// </summary>
internal sealed class SendFeature : IFeature
{
    public const string Feature = "send";

    private readonly SessionStore _store;
    private readonly MachineSet _machines;
    private readonly LiveHub _hub;

    public SendFeature(SessionStore store, MachineSet machines, LiveHub hub)
    {
        _store = store;
        _machines = machines;
        _hub = hub;
    }

    // Each is a write keyed on its session (a stop, on its run's), so one session's calls reach its machine in the order
    // sent, a machine that hangs holds up only its sessions' calls, and a closing socket doesn't cancel one already
    // queued; the machine's work runs outside the write, which only answers.
    public void Register(ICallRegistry calls)
    {
        calls.Write(Feature, "send", SessionsJson.Default.SendArgs, SessionsJson.Default.Sent, async (args, call) =>
        {
            var session = Session(args.Session);
            // A helper whose parent is on the machine that reaches this one takes its prompts from there.
            if (session.ParentSessionId is { } parent && _store.Get(parent) is not null)
                throw new CallFailed("a subagent's session takes prompts from its parent only");
            var runId = await _machines.For(session).SendAsync(session, args.Text, call.Cancellation)
                ?? throw new CallFailed("the running turn can't take a message yet");
            return _ => new Sent(runId);
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
            return await _machines.For(session).AnswerAsync(session, args.Ask, args.Answer, call.Cancellation)
                ? _ => new Done(true)
                : throw new CallFailed("that ask isn't open, or the answer doesn't fit it");
        }, args => OrderOf(args.Session));
        calls.Write(Feature, "stopTask", SessionsJson.Default.StopTaskArgs, SessionsJson.Default.Done, async (args, call) =>
        {
            var session = Session(args.Session);
            // A helper on another machine stops there.
            var stopped = _store.Get(args.Task) is { Helper: true, Machine: not null } helper && helper.ParentSessionId == session.SessionId
                ? _machines.For(helper).ActiveRun(helper) is { } run && await _machines.For(helper).StopAsync(helper, run, call.Cancellation)
                : await _machines.For(session).StopTaskAsync(session, args.Task, call.Cancellation);
            return stopped ? _ => new Done(true) : throw new CallFailed("that task isn't running, or can't be stopped");
        }, args => OrderOf(args.Session));
    }

    /// <summary>The order key of the writes that reach a session's machine.</summary>
    internal static string OrderOf(string sessionId) => $"session {sessionId}";

    private SessionInfo Session(string id) => _store.Get(id) ?? throw new CallFailed("unknown session");

    internal sealed record SendArgs([property: JsonRequired] string Session, [property: JsonRequired] string Text);

    internal sealed record Sent(string RunId);

    internal sealed record StopArgs([property: JsonRequired] string RunId);

    internal sealed record AnswerArgs([property: JsonRequired] string Session, [property: JsonRequired] string Ask,
        [property: JsonRequired] JsonElement Answer);

    internal sealed record StopTaskArgs([property: JsonRequired] string Session, [property: JsonRequired] string Task);

    internal sealed record Done(bool Ok);
}
