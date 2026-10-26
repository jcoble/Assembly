using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Assembly.Infrastructure.Http;

using Assembly.Application.Features.Preferences;
namespace Assembly.Infrastructure.Http;

/// <summary>
/// The preferences' calls. <c>POST /preferences/get {}</c> answers every saved value as <c>{ key: value }</c>;
/// <c>POST /preferences/set { values: { key: value } }</c> saves those keys and leaves the rest.
/// </summary>
internal sealed class PreferencesRoutes(PreferenceStore store)
{
    public static void Add(IEndpointRouteBuilder routes, FeatureContext context)
    {
        var preferences = new PreferencesRoutes(new PreferenceStore(context.Database));
        var calls = new FeatureCalls(routes, "preferences");
        calls.Map("get", preferences.GetAsync);
        calls.Map("set", preferences.SetAsync);
    }

    // A read passes the request's token to everything it awaits, so a load the page aborts stops here too.
    private async Task<Action<Utf8JsonWriter>> GetAsync(JsonElement body, CancellationToken cancellationToken)
    {
        var all = await store.ReadAllAsync(cancellationToken);
        return json =>
        {
            json.WriteStartObject();
            foreach (var (key, value) in all)
            {
                json.WritePropertyName(key);
                json.WriteRawValue(value);
            }
            json.WriteEndObject();
        };
    }

    // A save is the exception: once it has started it finishes, so it doesn't take the token (README.md).
    private Task<Action<Utf8JsonWriter>> SetAsync(JsonElement body, CancellationToken cancellationToken)
    {
        if (!body.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
            throw new CallFailed("set needs { values: { key: value } }");
        store.Save(values);
        return Task.FromResult<Action<Utf8JsonWriter>>(json => json.WriteNullValue());
    }
}
