using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assembly.Application.Common.Acp.V1;

/// <summary>
/// A version 1 initialize reply in the version 2 shape, so callers read one shape: <c>agentInfo</c> becomes
/// <c>info</c>; <c>agentCapabilities</c> becomes <c>capabilities</c>, with <c>sessionCapabilities</c> as
/// <c>session</c> and an image-capable prompt as <c>session.prompt.image</c>; each auth method's <c>id</c> becomes
/// <c>methodId</c>, and a method with no <c>type</c> is the agent's own sign-in.
/// </summary>
internal static class InitializeNormalizer
{
    public static JsonElement Normalize(JsonElement reply)
    {
        var shaped = JsonNode.Parse(reply.GetRawText())!.AsObject();
        if (shaped["agentInfo"] is { } info)
        {
            shaped.Remove("agentInfo");
            shaped["info"] ??= info;
        }
        if (shaped["agentCapabilities"] is JsonObject capabilities)
        {
            shaped.Remove("agentCapabilities");
            shaped["capabilities"] ??= Capabilities(capabilities);
        }
        if (shaped["authMethods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
            {
                if (method["methodId"] is null && method["id"] is { } id)
                {
                    method.Remove("id");
                    method["methodId"] = id;
                }
                method["type"] ??= "agent";
            }
        return UpdateNormalizer.Element(shaped);
    }

    private static JsonObject Capabilities(JsonObject capabilities)
    {
        var session = capabilities["sessionCapabilities"] as JsonObject ?? new JsonObject();
        capabilities.Remove("sessionCapabilities");
        if (capabilities["promptCapabilities"] is JsonObject prompt)
        {
            capabilities.Remove("promptCapabilities");
            if (prompt["image"]?.GetValueKind() == JsonValueKind.True)
                session["prompt"] = new JsonObject { ["image"] = new JsonObject() };
        }
        capabilities["session"] = session;
        return capabilities;
    }
}
