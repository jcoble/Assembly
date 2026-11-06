using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions.Configuration;
using Assembly.Domain.Sessions;

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
    private readonly IEngineDoor? _door;

    public ConfigFeature(SessionStore store, MachineSet machines, IEngineDoor? door = null)
    {
        _store = store;
        _machines = machines;
        _door = door;
    }

    public void Register(ICallRegistry calls)
    {
        calls.Read(Feature, "discover", SessionsJson.Default.DiscoverArgs, SessionsJson.Default.JsonElement, async (args, call) =>
        {
            var machine = _machines.Named(args.Machine) ?? throw new CallFailed("unknown machine");
            var result = await machine.DiscoverConfigAsync(args.Agent, args.TargetId, args.Cwd, call.Cancellation);
            Current(machine);
            return ComposerConfigJson.Json(result.Config, result.ModelOptions);
        });
        calls.Read(Feature, "warm", SessionsJson.Default.WarmArgs, SessionsJson.Default.JsonElement, async (args, call) =>
        {
            var expected = SendFeature.Source(args.ExpectedSource, call);
            if (expected is not null)
            {
                if (args.Machine is not null && args.Machine != expected.RouteMachine)
                    throw new CallBadArguments("The selected configuration route contradicts the source.");
                return Json(await Door().WarmSessionConfigAsync(args.Session, args.TargetId, expected, call));
            }
            var session = _store.Get(args.Session) ?? throw new CallFailed("unknown session");
            var machine = _machines.For(session);
            var config = await machine.WarmConfigAsync(session, args.TargetId, call.Cancellation);
            Current(machine);
            return Json(config);
        });
        calls.Read(Feature, "get", SessionsJson.Default.GetArgs, SessionsJson.Default.JsonElement, async (args, call) =>
        {
            var expected = SendFeature.Source(args.ExpectedSource, call);
            if (expected is not null) return Json(await Door().ReadSessionConfigAsync(args.Session, expected, call));
            var session = _store.Get(args.Session) ?? throw new CallFailed("unknown session");
            return Json(await _machines.For(session).ConfigAsync(session, call.Cancellation));
        });
        calls.Write(Feature, "set", SessionsJson.Default.SetArgs, SessionsJson.Default.JsonElement, async (args, call) =>
        {
            var expected = SendFeature.Source(args.ExpectedSource, call);
            if (args.ConfigId.Length == 0)
                throw new CallFailed("expected a configId");
            if (expected is not null)
            {
                var apply = await Door().PrepareSessionConfigAsync(args.Session, args.ConfigId, args.Value, args.TargetId, expected, call);
                return scope => Json(apply(scope));
            }
            var session = _store.Get(args.Session) ?? throw new CallFailed("unknown session");
            var machine = _machines.For(session);
            var config = Json(await machine.SetConfigAsync(session, args.ConfigId, args.Value, call.Cancellation, args.TargetId));
            Current(machine);
            return _ => config;
        }, args => SendFeature.OrderOf(args.Session));
    }

    private void Current(IMachine machine)
    {
        if (!ReferenceEquals(_machines.Named(machine.Name), machine)) throw new CallFailed("The selected machine changed.");
    }

    private static JsonElement Json(SessionConfig config) => ComposerConfigJson.Json(config);
    private IEngineDoor Door() => _door ?? throw new CallFailed("Source-bound configuration is unavailable.", "source_changed");

    internal sealed record DiscoverArgs([property: JsonRequired] string Agent, [property: JsonRequired] string TargetId,
        [property: JsonRequired] string Cwd, string? Machine = null);
    internal sealed record WarmArgs([property: JsonRequired] string Session, [property: JsonRequired] string TargetId, string? Machine = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExpectedSource? ExpectedSource = null);

    internal sealed record GetArgs([property: JsonRequired] string Session,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExpectedSource? ExpectedSource = null);

    internal sealed record SetArgs([property: JsonRequired] string Session, [property: JsonRequired] string ConfigId,
        [property: JsonRequired] string Value, string? TargetId = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExpectedSource? ExpectedSource = null);
}
