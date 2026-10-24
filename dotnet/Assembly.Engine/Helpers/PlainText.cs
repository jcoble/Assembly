using System.Text.Json;

namespace Assembly.Engine.Helpers;

/// <summary>A message row's blocks as plain text: blocks and list items on lines of their own, inline parts run together.</summary>
internal static class PlainText
{
    public static string Of(JsonElement blocks) => string.Join("\n\n", blocks.EnumerateArray().Select(block => Text(block, "\n")));

    private static string Text(JsonElement element, string between) => element.ValueKind switch
    {
        JsonValueKind.Object when element.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String => value.GetString()!,
        JsonValueKind.Object => string.Join("\n", element.EnumerateObject()
            .Where(property => property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            .Select(property => Text(property.Value, property.Name == "parts" ? "" : "\n"))),
        JsonValueKind.Array => string.Join(between, element.EnumerateArray().Select(item => Text(item, "\n"))),
        _ => "",
    };
}
