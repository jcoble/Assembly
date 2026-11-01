using System.Security.Cryptography;
using System.Text.Json;

namespace Assembly.Engine.Mcp;

/// <summary>Issues in-memory MCP bearer tokens bound to one engine session epoch.</summary>
public sealed class McpTokens
{
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _tokenBySession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpCaller> _callerByToken = new(StringComparer.Ordinal);

    /// <summary>The MCP endpoint sessions join, once the API is listening.</summary>
    public string? Url { get; set; }

    /// <summary>Creates a fresh server entry and invalidates any previous token for this session.</summary>
    public IReadOnlyList<JsonElement> ServersFor(string sessionId, long epoch)
    {
        var url = Url;
        if (url is null)
            return [];

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (_sync)
        {
            if (_tokenBySession.Remove(sessionId, out var oldToken))
                _callerByToken.Remove(oldToken);
            _tokenBySession[sessionId] = token;
            _callerByToken[token] = new McpCaller(sessionId, epoch);
        }

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("type", "http");
            json.WriteString("name", "assembly");
            json.WriteString("url", url);
            json.WriteStartArray("headers");
            json.WriteStartObject();
            json.WriteString("name", "Authorization");
            json.WriteString("value", $"Bearer {token}");
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return [document.RootElement.Clone()];
    }

    /// <summary>Returns the caller bound to <paramref name="token"/>, or null when it is unknown or revoked.</summary>
    public McpCaller? Caller(string token)
    {
        lock (_sync)
            return _callerByToken.GetValueOrDefault(token);
    }

    /// <summary>Revokes the current token for a session.</summary>
    public void Revoke(string sessionId)
    {
        lock (_sync)
        {
            if (_tokenBySession.Remove(sessionId, out var token))
                _callerByToken.Remove(token);
        }
    }
}
