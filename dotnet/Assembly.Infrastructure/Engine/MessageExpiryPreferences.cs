using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Preferences;

namespace Assembly.Infrastructure.Engine;

internal static class MessageExpiryPreferences
{
    internal static int ReadMinutes(IReadScope scope)
    {
        var json = scope.QueryFirst("SELECT value FROM preferences WHERE key=$key", r => r.Text(0),
            new SqlArg("$key", MessageExpiryPreference.Key));
        if (json is null) return 30;
        try
        {
            using var document = JsonDocument.Parse(json);
            return MessageExpiryPreference.Parse(document.RootElement);
        }
        catch (JsonException) { throw new CallFailed(MessageExpiryPreference.Error); }
    }
}
