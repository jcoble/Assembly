using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;

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

    public void Register(ICallRegistry calls)
    {
    }

    internal sealed record SendArgs([property: JsonRequired] string Session, [property: JsonRequired] string Text);

    internal sealed record Sent(string RunId);

    internal sealed record StopArgs([property: JsonRequired] string RunId);

    internal sealed record AnswerArgs([property: JsonRequired] string Session, [property: JsonRequired] string Ask,
        [property: JsonRequired] JsonElement Answer);

    internal sealed record StopTaskArgs([property: JsonRequired] string Session, [property: JsonRequired] string Task);

    internal sealed record Done(bool Ok);
}
