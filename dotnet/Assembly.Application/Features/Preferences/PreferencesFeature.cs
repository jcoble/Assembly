using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Preferences;

/// <summary>
/// The preferences over the socket (plan 5.2): the <c>preferences</c> topic, every saved preference, and
/// <c>preferences/set</c>, which saves one and publishes it. Keys and values are the page's own.
/// </summary>
internal sealed class PreferencesFeature : IFeature
{
    private const string All = "SELECT key, value FROM preferences ORDER BY key;";
    private const string Upsert =
        "INSERT INTO preferences(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";

    private readonly IAppDatabase _db;

    public PreferencesFeature(IAppDatabase db)
    {
        _db = db;
        db.Writer.Write("preferences.schema", scope => scope.Execute(PreferencesSchema.Create));
    }

    public void Register(ICallRegistry calls)
    {
        calls.Topic("preferences", PreferencesJson.Default.Preference, preference => preference.Key,
            (_, call) => _db.Query(All, Preference.Read, call.Cancellation));
        calls.Save("preferences", PreferencesJson.Default.JsonElement, (key, value, scope) =>
        {
            scope.Execute(Upsert, new SqlArg("$key", key), new SqlArg("$value", value.GetRawText()));
            scope.Publish("preferences", key, new Preference(key, value), PreferencesJson.Default.Preference);
        });
    }
}
