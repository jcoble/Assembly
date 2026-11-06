using System.Text.Json;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed partial class SessionHostTests
{
    [Fact]
    public async Task Proposed_edit_is_saved_and_pushed_with_its_permission_before_the_answer()
    {
        _host.Send("edit notes");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await Kinds(2);
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures/acp/tools.jsonl");
        using var entry = JsonDocument.Parse(File.ReadLines(fixture).Single(line => line.Contains("\"method\":\"session/request_permission\"", StringComparison.Ordinal)));
        var recorded = entry.RootElement.GetProperty("line").GetProperty("params");
        var permission = recorded.GetRawText().Replace(recorded.GetProperty("sessionId").GetString()!, FakeLink.AcpSessionId, StringComparison.Ordinal);
        await agent.RequestAsync(7, "session/request_permission", permission);
        var rows = await Rows(2);
        Assert.Equal(["tool", "ask"], rows.Select(row => row.Kind));
        var tool = rows[0];
        Assert.Equal(1, tool.Payload.GetProperty("added").GetInt32());
        Assert.Equal(1, tool.Payload.GetProperty("removed").GetInt32());
        Assert.Equal(tool.ItemId, rows[1].Payload.GetProperty("toolCallId").GetString());
        var patch = recorded.GetProperty("subject").GetProperty("toolCall").GetProperty("content")[0].GetProperty("patch").GetProperty("text").GetString();
        Assert.Equal(patch, _store.ToolCall(_session.SessionId, tool.ItemId!)!.Diff);
        var askId = rows[1].ItemId!["ask:".Length..];
        using var answer = JsonDocument.Parse("""{"optionId":"allow-once"}""");
        Assert.True(_host.Answer(askId, answer.RootElement));
        await agent.ResultAsync();
        await agent.UpdateAsync("{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"" + tool.ItemId + "\",\"status\":\"completed\"}");
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }
}
