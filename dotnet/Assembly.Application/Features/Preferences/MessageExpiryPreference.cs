using System.Text.Json;
using Assembly.Application.Common.Calls;

namespace Assembly.Application.Features.Preferences;

/// <summary>One strict field contract for both preference write doors and the receiving Engine.</summary>
public static class MessageExpiryPreference
{
    public const string Key = "messageExpiryMinutes";
    public const string Error = "messageExpiryMinutes: enter a positive integer from 1 to 2147483647.";

    public static int Parse(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var minutes) && minutes > 0
            ? minutes : throw new CallFailed(Error);

    public static void Validate(string key, JsonElement value)
    {
        if (key == Key) Parse(value);
    }
}
