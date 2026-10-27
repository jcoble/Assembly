using Microsoft.AspNetCore.Routing;

namespace Assembly.Infrastructure.Http;

/// <summary>
/// The features every machine serves, one line each: the desktop and each remote host call this. A feature that works on
/// a machine's own files, git or terminals goes here; one that only the desktop's window needs goes in the desktop's list
/// (<c>DesktopFeatureList.cs</c>, next to this one). None yet.
/// </summary>
public static class MachineFeatureList
{
    public static void Add(IEndpointRouteBuilder routes, FeatureContext context)
    {
    }
}
