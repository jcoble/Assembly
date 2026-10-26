using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Assembly.Application.Features.Chat.Helpers;

/// <summary>
/// Each session's token for the Assembly MCP server, which names its thread: a new one each time the session opens,
/// gone when it closes. Kept in memory here: never logged, never in a URL. (Claude Code's SDK hands MCP servers to
/// its CLI as --mcp-config, so its token is on that process's command line.)
/// </summary>
public sealed class SessionTokens
{
    private readonly ConcurrentDictionary<string, string> _tokens = new();
    private readonly ConcurrentDictionary<string, string> _sessionOfToken = new();

    /// <summary>The MCP endpoint sessions join, once the API is listening; until then sessions join no server.</summary>
    public string? Url { get; set; }

    /// <summary>The MCP servers the thread's session joins: this machine's, with a new token for the thread.</summary>
    public IReadOnlyList<JsonElement> ServersFor(string sessionId)
    {
        if (Url is not { } url)
            return [];
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessionOfToken[token] = sessionId;
        if (_tokens.TryGetValue(sessionId, out var old))
            _sessionOfToken.TryRemove(old, out _);
        _tokens[sessionId] = token;
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
        return [JsonDocument.Parse(buffer.ToArray()).RootElement.Clone()];
    }

    /// <summary>The thread's session closed: its token stops working.</summary>
    public void Revoke(string sessionId)
    {
        if (_tokens.TryRemove(sessionId, out var token))
            _sessionOfToken.TryRemove(token, out _);
    }

    /// <summary>The thread whose session holds <paramref name="token"/>, or null.</summary>
    public string? SessionOf(string token) => _sessionOfToken.GetValueOrDefault(token);
}
