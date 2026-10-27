using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// A session's config over the socket (plan C1), on whichever machine runs its agent: <c>config/get</c> answers
/// <c>{options, choices, commands}</c>, as GET /sessions/{id}/config does, and <c>config/set</c> chooses a value,
/// answering the same. They stay calls: a change the agent makes mid-turn only updates the stored copy, so the page
/// sees it on its next get.
/// </summary>
internal sealed class ConfigFeature : IFeature
{
    public const string Feature = "config";

    private readonly SessionStore _store;
    private readonly MachineSet _machines;

    public ConfigFeature(SessionStore store, MachineSet machines)
    {
        _store = store;
        _machines = machines;
    }

    public void Register(ICallRegistry calls)
    {
    }

    internal sealed record GetArgs([property: JsonRequired] string Session);

    internal sealed record SetArgs([property: JsonRequired] string Session, [property: JsonRequired] string ConfigId,
        [property: JsonRequired] string Value);
}
