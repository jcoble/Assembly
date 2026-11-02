using System.Text.Json;

using Assembly.Application.Common.Agents;
namespace Assembly.Application.Common.Acp;

/// <summary>
/// Reads an agent's session config options (ACP <c>configOptions</c>: <c>[{ id, name, category, type, currentValue,
/// options: [{ value, name }] }]</c>), which stay raw JSON everywhere else, so the menus show whatever the agent offers.
/// </summary>
public static class ConfigOptions
{
    /// <summary>Each select option's id, current value and the values it takes, in the agent's order.</summary>
    public static IEnumerable<(string Id, string? Current, IReadOnlyList<string> Values)> Selects(JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var option in options.EnumerateArray())
        {
            if (Text(option, "type") != "select" || Text(option, "id") is not { } id)
                continue;
            var values = option.TryGetProperty("options", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(value => Text(value, "value")).OfType<string>().ToList()
                : [];
            yield return (id, Text(option, "currentValue"), values);
        }
    }

    /// <summary>The name of the current value of the agent's model option (category <c>model</c>), if it has one.</summary>
    public static string? ModelName(JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var option in options.EnumerateArray())
        {
            if (Text(option, "category") != "model" || Text(option, "currentValue") is not { } current
                || !option.TryGetProperty("options", out var list) || list.ValueKind != JsonValueKind.Array)
                continue;
            return list.EnumerateArray().Where(value => Text(value, "value") == current).Select(value => Text(value, "name")).FirstOrDefault() ?? current;
        }
        return null;
    }

    /// <summary>The current raw model selector, separate from its display name.</summary>
    public static string? ModelValue(JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Array) return null;
        return options.EnumerateArray().Where(option => Text(option, "category") == "model")
            .Select(option => Text(option, "currentValue")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    /// <summary>The values and labels of the existing model selector, in the provider's order.</summary>
    public static IReadOnlyList<(string Id, string Name)> Models(JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Array) return [];
        foreach (var option in options.EnumerateArray())
        {
            if (Text(option, "category") != "model" || Text(option, "type") != "select"
                || !option.TryGetProperty("options", out var list) || list.ValueKind != JsonValueKind.Array)
                continue;
            return list.EnumerateArray().Where(value => Text(value, "value") is not null)
                .Select(value => (Text(value, "value")!, Text(value, "name") ?? Text(value, "value")!)).ToList();
        }
        return [];
    }

    private static string? Text(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
