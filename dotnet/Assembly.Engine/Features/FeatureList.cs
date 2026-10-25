using Microsoft.AspNetCore.Routing;
using Assembly.Engine.Preferences;

namespace Assembly.Engine.Features;

/// <summary>Every feature's calls, one line each. The Mac and the remote host both call this.</summary>
public static class FeatureList
{
    public static void Add(IEndpointRouteBuilder routes, FeatureContext context)
    {
        PreferencesRoutes.Add(routes, context);
    }
}
