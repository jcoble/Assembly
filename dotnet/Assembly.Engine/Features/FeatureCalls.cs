using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Assembly.Engine.Api;

namespace Assembly.Engine.Features;

/// <summary>
/// One feature's calls, and the only way a feature adds a route (scripts/lint.d/40-routes.sh): <c>POST
/// /&lt;feature&gt;/&lt;method&gt;</c> with a JSON object as the body answers <c>{result}</c>, 400 <c>{error}</c> for a
/// body that isn't an object or a call that throws <see cref="CallFailed"/>, and 404 <c>{error}</c> for a method the
/// feature doesn't have. Every handler gets <c>HttpContext.RequestAborted</c>, so a request the page aborts stops the
/// work it started. The host adds the token guard and 127.0.0.1; the transport stays in this class.
/// </summary>
internal sealed class FeatureCalls
{
    /// <summary>A call: its body and the request's token; it answers what writes the result's value.</summary>
    public delegate Task<Action<Utf8JsonWriter>> Handler(JsonElement body, CancellationToken cancellationToken);

    private readonly string _feature;
    private readonly Dictionary<string, Handler> _methods = [];

    public FeatureCalls(IEndpointRouteBuilder routes, string feature)
    {
        _feature = feature;
        routes.MapPost($"/{feature}/{{method}}", CallAsync);
    }

    public void Map(string method, Handler handler) => _methods.Add(method, handler);

    private async Task CallAsync(HttpContext context)
    {
        var method = RequestValues.Route(context, "method");
        // The call's name only, never its body or answer.
        Console.WriteLine($"call: {_feature}/{method}");
        if (!_methods.TryGetValue(method, out var handler))
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status404NotFound, $"{_feature} has no call {method}");
            return;
        }
        if (await JsonReply.ReadObjectAsync(context) is not { } body)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "expected a JSON object");
            return;
        }

        Action<Utf8JsonWriter> result;
        try
        {
            result = await handler(body, context.RequestAborted);
        }
        catch (CallFailed error)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, error.Message);
            return;
        }

        await JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
        {
            json.WriteStartObject();
            json.WritePropertyName("result");
            result(json);
            json.WriteEndObject();
        });
    }
}
