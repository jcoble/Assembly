using Microsoft.AspNetCore.Routing;
using Assembly.Engine.Features;
using Assembly.Mac.Preferences;

namespace Assembly.Mac.Features;

/// <summary>
/// The features only the Mac serves, one line each, for its window: settings and the like. A feature that every
/// machine serves goes in <see cref="MachineFeatureList"/> instead.
/// </summary>
public static class MacFeatureList
{
    public static void Add(IEndpointRouteBuilder routes, FeatureContext context)
    {
        PreferencesRoutes.Add(routes, context);
    }
}
