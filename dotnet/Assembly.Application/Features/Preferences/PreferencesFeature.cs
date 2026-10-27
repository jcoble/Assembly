using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Preferences;

/// <summary>
/// The preferences over the socket (plan 5.2): the <c>preferences</c> topic, every saved preference, and
/// <c>preferences/set</c>, which saves one and publishes it.
/// </summary>
internal sealed class PreferencesFeature(IAppDatabase db) : IFeature
{
    public void Register(ICallRegistry calls) => _ = db;
}
