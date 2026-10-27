using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
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

    public ConfigFeature(SessionStore store, MachineSet machines)
    {
        _store = store;
        _machines = machines;
    }

    public void Register(ICallRegistry calls)
    {
        calls.Read(Feature, "get", SessionsJson.Default.GetArgs, SessionsJson.Default.JsonElement, async (args, call) =>
        {
            var session = _store.Get(args.Session) ?? throw new CallFailed("unknown session");
            return Json(await _machines.For(session).ConfigAsync(session, call.Cancellation));
        });
        calls.Write(Feature, "set", SessionsJson.Default.SetArgs, SessionsJson.Default.JsonElement, async (args, call) =>
        {
            var session = _store.Get(args.Session) ?? throw new CallFailed("unknown session");
            var config = Json(await _machines.For(session).SetConfigAsync(session, args.ConfigId, args.Value, call.Cancellation));
            return _ => config;
        });
    }

    // The agent's options and commands are its own JSON, kept as it sent them.
    private static JsonElement Json(SessionConfig config)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WritePropertyName("options");
            if (config.Options is { } options)
                json.WriteRawValue(options);
            else
                json.WriteNullValue();
            json.WriteStartObject("choices");
            foreach (var (id, value) in config.Choices)
                json.WriteString(id, value);
            json.WriteEndObject();
            json.WritePropertyName("commands");
            if (config.Commands is { } commands)
                json.WriteRawValue(commands);
            else
                json.WriteNullValue();
            json.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    internal sealed record GetArgs([property: JsonRequired] string Session);

    internal sealed record SetArgs([property: JsonRequired] string Session, [property: JsonRequired] string ConfigId,
        [property: JsonRequired] string Value);
}
