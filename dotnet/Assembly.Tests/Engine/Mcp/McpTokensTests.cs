using System.Text.Json;
using Assembly.Engine.Mcp;

namespace Assembly.Tests.Engine.Mcp;

public sealed class McpTokensTests
{
    [Fact]
    public void New_token_replaces_old_and_carries_epoch()
    {
        var tokens = new McpTokens { Url = "http://127.0.0.1:5/mcp" };
        var first = Bearer(tokens.ServersFor("s1", 0));
        var second = Bearer(tokens.ServersFor("s1", 1));

        Assert.Null(tokens.Caller(first));
        Assert.Equal(new McpCaller("s1", 1), tokens.Caller(second));
    }

    [Fact]
    public void Revoke_stops_the_token()
    {
        var tokens = new McpTokens { Url = "http://127.0.0.1:5/mcp" };
        var token = Bearer(tokens.ServersFor("s1", 0));

        tokens.Revoke("s1");

        Assert.Null(tokens.Caller(token));
    }

    [Fact]
    public void No_url_joins_no_server()
    {
        Assert.Empty(new McpTokens().ServersFor("s1", 0));
    }

    [Fact]
    public void Entry_is_the_acp_http_shape()
    {
        const string url = "http://127.0.0.1:5/mcp";
        var tokens = new McpTokens { Url = url };
        var entry = tokens.ServersFor("s1", 0)[0];

        Assert.Equal("http", entry.GetProperty("type").GetString());
        Assert.Equal("assembly", entry.GetProperty("name").GetString());
        Assert.Equal(url, entry.GetProperty("url").GetString());
        var header = Assert.Single(entry.GetProperty("headers").EnumerateArray());
        Assert.Equal("Authorization", header.GetProperty("name").GetString());
        var token = header.GetProperty("value").GetString()!;
        Assert.StartsWith("Bearer ", token, StringComparison.Ordinal);
        Assert.Equal(64, token["Bearer ".Length..].Length);
        Assert.Equal(new McpCaller("s1", 0), tokens.Caller(token["Bearer ".Length..]));
    }

    private static string Bearer(IReadOnlyList<JsonElement> servers) =>
        servers[0].GetProperty("headers")[0].GetProperty("value").GetString()!["Bearer ".Length..];
}
