using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Assembly.Engine.Live;

namespace Assembly.Mac.Debug;

/// <summary>
/// <c>POST /debug/drop {ms}</c>: closes every live socket, so the webview reopens them from its watermarks, as it
/// would after a dropped link. <c>ms</c> will hold a remote link down that long once there is one; nothing reads it yet.
/// </summary>
internal sealed class DebugRoutes(LiveHub hub)
{
    public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/debug/drop", Drop);

    private Task Drop(HttpContext context)
    {
        hub.CloseSubscriptions();
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }
}
