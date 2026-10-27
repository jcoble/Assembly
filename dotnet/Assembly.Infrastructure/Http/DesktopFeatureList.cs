using Microsoft.AspNetCore.Routing;
using Assembly.Infrastructure.Http;
using Assembly.Application.Features.Preferences;

namespace Assembly.Infrastructure.Http;

/// <summary>
/// The features only the desktop serves, one line each, for its window: settings and the like. A feature that every
/// machine serves goes in <see cref="MachineFeatureList"/> instead.
/// </summary>
public static class DesktopFeatureList
{
    public static void Add(IEndpointRouteBuilder routes, FeatureContext context)
    {
        PreferencesRoutes.Add(routes, context);
    }
}
