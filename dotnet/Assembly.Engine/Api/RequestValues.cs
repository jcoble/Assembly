using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Assembly.Engine.Api;

/// <summary>Route and query values, read the same way everywhere.</summary>
internal static class RequestValues
{
    public static string Route(HttpContext context, string name) => (string)context.Request.RouteValues[name]!;

    public static long? QueryLong(HttpContext context, string name) =>
        long.TryParse(context.Request.Query[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
