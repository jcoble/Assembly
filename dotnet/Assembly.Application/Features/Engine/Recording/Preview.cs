using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Assembly.Application.Features.Engine.Recording;

public static class Preview
{
    private const int Limit = 4096;

    public static string Create(JsonElement value)
    {
        var text = new StringBuilder();
        Describe(value, text);
        return text.ToString();
    }

    private static void Describe(JsonElement value, StringBuilder text)
    {
        if (text.Length == Limit) return;
        var count = value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Count(),
            JsonValueKind.Array => value.GetArrayLength(),
            JsonValueKind.String => Encoding.UTF8.GetByteCount(value.GetString()!),
            JsonValueKind.Undefined => 0,
            _ => Encoding.UTF8.GetByteCount(value.GetRawText())
        };
        // Only ASCII kind names, counts and delimiters enter the preview. Truncation is always UTF-8 safe.
        var description = $"{value.ValueKind}({count.ToString(CultureInfo.InvariantCulture)}) ";
        text.Append(description.AsSpan(0, Math.Min(description.Length, Limit - text.Length)));
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                Describe(property.Value, text);
                if (text.Length == Limit) break;
            }
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray())
            {
                Describe(element, text);
                if (text.Length == Limit) break;
            }
    }
}
