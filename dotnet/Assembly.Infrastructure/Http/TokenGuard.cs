using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

using Assembly.Infrastructure.Mcp;
namespace Assembly.Infrastructure.Http;

/// <summary>
/// Every HTTP request needs the token as <c>Authorization: Bearer</c>. Preflights are answered first, as they carry
/// no token. <c>/mcp</c> takes a session's own token instead (<see cref="McpServerHost"/>),
/// and <c>/socket</c>, <c>/socket/bulk</c> and <c>/socket/terminal</c> take it in its first frame (<see cref="Socket.SocketConnection"/>).
/// </summary>
internal sealed class TokenGuard
{
    private readonly byte[] _token;

    public TokenGuard(string token) => _token = Encoding.UTF8.GetBytes(token);

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers.CacheControl = "no-store";

        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type";
            context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (context.Request.Path != "/mcp" && context.Request.Path != "/socket" && context.Request.Path != "/socket/bulk" && context.Request.Path != "/socket/terminal" && !Carries(context))
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
        return given is { Length: > 0 } && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), _token);
    }
}
