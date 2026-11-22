using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Features.Projects;

namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class GitSocketTests
{
    [Fact]
    public async Task A_project_without_any_session_reads_its_actual_checkout_pull_request()
    {
        var root = Path.Combine(_checkouts.FullName, "registered-project");
        await MakeRepoAsync(root);
        await GitAsync(root, "remote", "add", "origin", "https://github.com/owner/repo");
        var store = new ProjectStore(_db, TimeProvider.System);
        var project = _db.Writer.Write("fixture.project", scope => store.AddOrRestore(scope, "local", root, "Project", "github.com/owner/repo").Project);
        using var socket = await ProjectBulk();
        await SendAsync(socket, JsonSerializer.Serialize(new
        {
            t = "call",
            id = 1,
            feature = "git",
            method = "pr",
            args = new { projectId = project.Id, number = 7, expectedSource = new { machine = _operations.Machine, generation = 0, routeMachine = (string?)null } }
        }));
        using var answer = JsonDocument.Parse(await ReadAsync(socket));
        var detail = answer.RootElement.GetProperty("result");
        Assert.Equal("owner/repo", detail.GetProperty("repository").GetString());
        Assert.Equal(root, detail.GetProperty("localRoot").GetString());
        Assert.Equal(7, detail.GetProperty("number").GetInt32());
        Assert.Equal(0L, _db.QueryFirst("SELECT COUNT(*) FROM session_info", row => row.Long(0), default));
    }

    private async Task<ClientWebSocket> ProjectBulk()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(_address + "/bulk"), default);
        await SendAsync(socket, """{"t":"hello","token":"git-token","protocol":2}""");
        await ReadAsync(socket);
        return socket;
    }
}
