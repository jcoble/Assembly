using System.Text.Json;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.V2;
using Assembly.Domain.Events;

namespace Assembly.Tests.Acp;

public sealed class TerminalUpdateTests
{
    private static SessionNotification Update(string json) => new("provider", JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void Terminal_reference_and_patch_snapshots_are_immutable_and_explicit_null_clears()
    {
        var reader = new UpdateReader();
        var first = Assert.Single(reader.Read(Update("""{"sessionUpdate":"tool_call_update","toolCallId":"C","name":"Exec","title":"command","kind":"execute","status":"in_progress","rawInput":{"x":1},"rawOutput":"old","locations":[{"path":"file"}],"content":[{"type":"terminal","terminalId":"T"}]}""")).OfType<ToolCallChanged>());
        var cleared = Assert.Single(reader.Read(Update("""{"sessionUpdate":"tool_call_update","toolCallId":"C","name":null,"title":null,"kind":null,"status":null,"rawInput":null,"rawOutput":null,"locations":null,"content":null}""")).OfType<ToolCallChanged>());
        Assert.Equal("pending", cleared.Fields.Status);
        Assert.Null(cleared.Fields.Name); Assert.Null(cleared.Fields.Title);
        Assert.Null(cleared.Fields.Input); Assert.Null(cleared.Fields.Output); Assert.Null(cleared.Fields.Paths);
        Assert.Equal("other", cleared.Fields.Kind);
        // JSON keeps this test compilable against the old event contract: it fails on behavior first.
        var before = JsonSerializer.SerializeToElement(first.Fields);
        Assert.Equal("T", before.GetProperty("TerminalIds")[0].GetString());
        Assert.Equal("provider", before.GetProperty("AcpSessionId").GetString());
        Assert.True(before.GetProperty("Snapshot").GetBoolean());
        Assert.Empty(JsonSerializer.SerializeToElement(cleared.Fields).GetProperty("TerminalIds").EnumerateArray());
    }

    [Theory]
    [InlineData("completed", "completed")]
    [InlineData("failed", "failed")]
    [InlineData("cancelled", "stopped")]
    public void Retention_ignores_foreground_extensions_and_settles_only_matching_explicit_terminal_status(string status, string expected)
    {
        var reader = new UpdateReader();
        reader.Read(Update("""{"sessionUpdate":"tool_call_update","toolCallId":"C","status":"in_progress"}"""));
        var idle = reader.Read(Update("""{"sessionUpdate":"state_update","state":"idle","stopReason":"cancelled"}"""));
        Assert.Equal("C", Assert.Single(idle.OfType<TaskChanged>()).ToolCallId);
        Assert.IsType<TurnEnded>(idle[^1]);
        foreach (var patch in new[] { "{}", "{\"status\":null}", "{\"status\":\"pending\"}", "{\"status\":\"future\"}" })
        {
            var fields = patch[1..^1];
            Assert.Empty(reader.Read(Update("{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"C\"" + (fields.Length == 0 ? "" : "," + fields) + "}")).OfType<TaskChanged>());
        }
        Assert.Empty(reader.Read(Update("""{"sessionUpdate":"state_update","state":"unknown","background":[]}""")).OfType<TaskChanged>());
        Assert.Empty(reader.Read(Update("""{"sessionUpdate":"tool_call_update","toolCallId":"other","status":"completed"}""")).OfType<TaskChanged>());
        var ended = Assert.Single(reader.Read(Update("{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"C\",\"status\":\"" + status + "\"}")).OfType<TaskChanged>());
        Assert.Equal(expected, ended.State);
        Assert.Empty(reader.Read(Update("{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"C\",\"status\":\"" + status + "\"}")).OfType<TaskChanged>());
    }

    [Fact]
    public void Genuine_codex_background_capture_retains_before_end_and_delivers_late_bytes()
    {
        var events = Assembly.Tests.Fixtures.FixtureReplay.Events("acp-v2/codex-terminal-background.jsonl");
        var task = Assert.Single(events.OfType<TaskChanged>(), t => t.Running);
        Assert.False(task.CanStop); Assert.True(task.Tracked);
        Assert.Equal(task.TaskId, task.ToolCallId);
        var end = Assert.Single(events.OfType<TurnEnded>());
        Assert.True(Array.IndexOf(events.ToArray(), task) < Array.IndexOf(events.ToArray(), end));
        var terminal = Assert.Single(events, e => e.GetType().Name == "TerminalChanged" && JsonSerializer.SerializeToElement(e, e.GetType()).GetProperty("Data").ValueKind == JsonValueKind.String);
        Assert.Equal("ZmluaXNoZWQK", JsonSerializer.SerializeToElement(terminal, terminal.GetType()).GetProperty("Data").GetString());
        Assert.True(terminal.At - end.At > 5000);
        Assert.Equal("completed", events.OfType<TaskChanged>().Last().State);
    }
}
