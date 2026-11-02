using System.Globalization;
using System.Security.Claims;
using Assembly.Engine.Mcp;
using Microsoft.AspNetCore.Http;

namespace Assembly.Infrastructure.Mcp;

internal sealed class McpBearer(McpTokens tokens)
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        var caller = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? tokens.Caller(authorization[7..]) : null;
        if (caller is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var identity = new ClaimsIdentity(
            [new Claim("assembly:session", caller.SessionId),
             new Claim("assembly:epoch", caller.Epoch.ToString(CultureInfo.InvariantCulture))],
            authenticationType: "Assembly.McpBearer");
        context.User = new ClaimsPrincipal(identity);
        await next(context);
    }
}
