using System.Text.Json;

namespace Assembly.Infrastructure.Engine;

internal static class SessionLimitPreferences
{
    internal static int Depth(string? json) => Read(json, 1);
    internal static int Children(string? json) => Read(json, 5);

    private static int Read(string? json, int fallback)
    {
        if (json is null) return fallback;
        try
        {
            using var document = JsonDocument.Parse(json);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Number ||
                !value.TryGetDouble(out var number) || !double.IsFinite(number) ||
                number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue)
                return fallback;
            return (int)number;
        }
        catch (JsonException) { return fallback; }
    }
}
