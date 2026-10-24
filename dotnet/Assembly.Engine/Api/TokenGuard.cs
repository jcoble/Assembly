using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Assembly.Engine.Api;

/// <summary>
/// Every request needs the token as <c>Authorization: Bearer</c>; only the live socket may pass it as <c>?token=</c>,
/// since a browser can't set headers on a WebSocket. Preflights are answered first, as they carry no token, and
/// only the webview's origins may read the answers. <c>/mcp</c> takes a session's own token instead (<see cref="McpRoutes"/>).
/// </summary>
internal sealed class TokenGuard
{
    // The shell's page, and vite dev: main on 1430, each lane worktree on 1431 to 1439.
    private static readonly string[] Origins =
        ["tauri://localhost", "http://tauri.localhost", .. Enumerable.Range(1430, 10).Select(port => $"http://localhost:{port}")];

    private readonly byte[] _token;

    public TokenGuard(string token) => _token = Encoding.UTF8.GetBytes(token);

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (Origins.Contains(origin))
            context.Response.Headers.AccessControlAllowOrigin = origin;
        context.Response.Headers.CacheControl = "no-store";

        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type";
            context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (context.Request.Path != "/mcp" && !Carries(context))
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status401Unauthorized, "unauthorized");
            return;
        }

        await next(context);
    }

    private bool Carries(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var given = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : null;
        if (given is null && context.Request.Path == "/live")
            given = context.Request.Query["token"].ToString();
        return given is { Length: > 0 } && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), _token);
    }
}
