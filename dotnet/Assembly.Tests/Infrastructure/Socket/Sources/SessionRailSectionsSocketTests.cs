using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Tests.Infrastructure.Http;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed class SessionRailSectionsSocketTests
{
    [Fact]
    public async Task Rail_pages_owner_attention_and_lifecycle_shelves_with_permanent_handles()
    {
        await using var host = new TwoHosts.Host();
        const string failed = "failed-session", question = "question-session", working = "working-session",
            done = "done-session", settled = "settled-session", stopped = "stopped-session";
        foreach (var id in new[] { failed, question, working, done, settled, stopped })
        {
            host.Fixture.SeedSession(id);
            host.Fixture.Db.Writer.Write("test.project-session", scope =>
            {
                host.Sessions.ProjectEngineSession(scope, new(id, "codex", "/test", "Test", null, 1), null, "/test");
                return 0;
            });
        }
        host.Fixture.SeedSession("child-session", "ended", parentId: working);
        host.Fixture.SeedSession("question-child", "ended", parentId: question);
        host.Fixture.SeedSession("failed-child", "ended", parentId: failed);
        host.Fixture.SeedRun(ownerSessionId: stopped, state: "needs_me");
        host.Fixture.SeedStep(state: "waiting");
        host.Fixture.Db.Writer.Write("test.stopped-step", scope =>
        {
            scope.Execute("UPDATE engine_run SET definition_hash='test-flow' WHERE id='r'");
            return scope.Execute("UPDATE engine_step SET outcome_reason='interrupted' WHERE id='step'");
        });
        Assert.True(host.Fixture.Apply(new OpenNeed(question, null, null, "question", "Which counter?",
            [], null, null, null), "question-need", actor: new(ActorKind.System, "host")).Applied);
        var rows = RowStore.Open(host.Fixture.Db);
        var failure = new RowBatch();
        failure.Rows.Add(new("run", null, RowKind.Turn, JsonSerializer.SerializeToElement(new { state = "failed" }), 1));
        failure.Rows.Add(new("run", null, RowKind.Error, JsonSerializer.SerializeToElement(new { message = "Closed" }), 2));
        failure.Rows.Add(new("run", "answer", RowKind.Message, JsonSerializer.SerializeToElement(new
        {
            blocks = new[] { new { text = new[] { new { value = "Earlier answer" } } } }
        }), 1));
        rows.Commit(failed, failure);
        rows.Commit(question, failure);
        rows.Commit(stopped, failure);
        var preview = new RowBatch
        {
            Rows = { new("run", "answer", RowKind.Message, failure.Rows[2].Payload, 1) }
        };
        rows.Commit(working, preview);
        rows.Commit(done, preview);
        await host.Start();

        using var socket = await ApiTests.OpenOwnerSocketAsync(host.Address, "isolated-token");
        var request = 0;
        async Task<JsonElement> Call(WebSocket client, string method, object args) =>
            ApiTests.Result(await ApiTests.SocketRequestAsync(client, ++request, "sessions", method, JsonSerializer.Serialize(args)));
        await Call(socket, "mark", new { session = done, state = "done" });
        await Call(socket, "mark", new { session = settled, state = "settled" });

        var first = await Call(socket, "rail", new { });
        var visible = first.GetProperty("sessions").EnumerateArray().ToArray();
        static string? Text(JsonElement row, string name) => row.GetProperty(name).GetString();
        Assert.Equal(new[] { ("needs", 3), ("working", 1), ("done", 2) },
            visible.Where(row => Text(row, "kind") == "section")
                .Select(row => (Text(row, "section")!, row.GetProperty("count").GetInt32())));
        Assert.DoesNotContain(visible, row => Text(row, "sessionId") is done or settled);

        var open = await Call(socket, "rail", new { folded = new[] { "+done" } });
        var expected = open.GetProperty("sessions").EnumerateArray().ToArray();
        var paged = new List<string?>();
        JsonElement? after = null;
        do
        {
            var page = await Call(socket, "rail", new { folded = new[] { "+done" }, limit = 2, after });
            paged.AddRange(page.GetProperty("sessions").EnumerateArray().Select(row => Text(row, "sessionId")));
            Assert.True(paged.Count <= expected.Length);
            after = page.GetProperty("next") is { ValueKind: JsonValueKind.Object } cursor ? cursor : null;
        } while (after is not null);
        Assert.Equal(expected.Select(row => Text(row, "sessionId")), paged);
        Assert.Equal(paged.Count, paged.Distinct().Count());

        var sessions = expected.Where(row => Text(row, "kind") == "session")
            .ToDictionary(row => Text(row, "sessionId")!);
        Assert.Equal("failed", Text(sessions[failed], "statusKind"));
        Assert.Equal("Closed", Text(sessions[failed], "lastError"));
        Assert.Equal("need", Text(sessions[question], "statusKind"));
        Assert.Equal("Which counter?", Text(sessions[question], "statusText"));
        Assert.True(sessions[question].GetProperty("needsYou").GetBoolean());
        Assert.True(sessions[failed].GetProperty("needsYou").GetBoolean());
        Assert.Equal("step", Text(sessions[stopped], "statusKind"));
        Assert.Equal("interrupted", Text(sessions[stopped], "statusReason"));
        Assert.Equal("preview", Text(sessions[done], "statusKind"));
        Assert.Equal("subagents", Text(sessions[working], "statusKind"));
        Assert.Equal(1, sessions[working].GetProperty("subagentCount").GetInt32());
        Assert.Equal(1, sessions[working].GetProperty("subagentDone").GetInt32());
        var handles = sessions.ToDictionary(pair => pair.Key, pair => Text(pair.Value, "handle"));
        Assert.All(handles.Values, handle => Assert.False(string.IsNullOrWhiteSpace(handle)));
        Assert.Equal(handles.Count, handles.Values.Distinct().Count());

        socket.Dispose();
        await host.Stop();
        await host.Restart();
        using var restarted = await ApiTests.OpenOwnerSocketAsync(host.Address, "isolated-token");
        var again = await Call(restarted, "rail", new { folded = new[] { "+done" } });
        foreach (var row in again.GetProperty("sessions").EnumerateArray().Where(row => Text(row, "kind") == "session"))
            Assert.Equal(handles[Text(row, "sessionId")!], Text(row, "handle"));
    }
}
