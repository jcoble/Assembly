using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.GitHub;
namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class GitSocketTests
{
    // Pins: g9#20
    [Fact]
    public async Task Pull_request_detail_uses_the_bulk_door_and_denial_leaves_the_normal_socket_usable()
    {
        var session = _sessions.Create("claude", "https://github.com/o/r"); var root = _sessions.Item(session).Root!;
        await MakeRepoAsync(root); await GitAsync(root, "remote", "add", "origin", "https://github.com/owner/repo");
        using var normal = await OpenAsync();
        var call = JsonSerializer.Serialize(new { t = "call", id = 1, feature = "git", method = "pr", args = new { session = session.SessionId, number = 7 } });
        await SendAsync(normal, call); await AssertErrorAsync(normal, "git/pr is on /socket/bulk");
        await CallAsync(normal, 2, "git", "status", session.SessionId);
        using var status = JsonDocument.Parse(await ReadAsync(normal)); Assert.Equal("result", status.RootElement.GetProperty("t").GetString());
        using var bulk = new ClientWebSocket(); await bulk.ConnectAsync(new Uri(_address + "/bulk"), default);
        await SendAsync(bulk, """{"t":"hello","token":"git-token","protocol":1}"""); await ReadAsync(bulk);
        await SendAsync(bulk, call); using var detail = JsonDocument.Parse(await ReadAsync(bulk));
        Assert.Equal(7, detail.RootElement.GetProperty("result").GetProperty("number").GetInt32());
        Assert.Equal("owner/repo", detail.RootElement.GetProperty("result").GetProperty("repository").GetString());
    }
    private sealed class SocketGh : IGh
    {
        public Task<GhRun> RunAsync(string folder, IReadOnlyList<string> args, string? input, TimeSpan limit, CancellationToken ct) => Task.FromResult(new GhRun(0, args[0] == "pr" ? """{"number":7,"title":"PR","headRefOid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseRefOid":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","state":"OPEN"}""" : "[]", ""));
    }
}
