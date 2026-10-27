using System.Text.Json;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Preferences;

/// <summary>One preference: its key, and its value as the page saved it.</summary>
internal sealed record Preference(string Key, JsonElement Value)
{
    /// <summary>A row of <c>SELECT key, value FROM preferences</c>.</summary>
    public static Preference Read(IRow row)
    {
        using var value = JsonDocument.Parse(row.Text(1));
        return new Preference(row.Text(0), value.RootElement.Clone());
    }
}
