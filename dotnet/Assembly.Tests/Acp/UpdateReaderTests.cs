using System.Text.Json;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.V2;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;

namespace Assembly.Tests.Acp;

public sealed class UpdateReaderTests
{
    private static SessionNotification Update(string json) => new("s", JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void Chunks_and_replacement_keep_one_message_with_the_replaced_text()
    {
        var reader = new UpdateReader(); var converter = new RowConverter();
        converter.Apply(new TurnStarted(0, "run", "hi"));
        foreach (var line in new[] {
            """{"sessionUpdate":"agent_message_chunk","messageId":"m","content":{"type":"text","text":"old"}}""",
            """{"sessionUpdate":"agent_message","messageId":"m","content":[{"type":"text","text":"new"}]}""",
            """{"sessionUpdate":"agent_message_chunk","messageId":"m","content":{"type":"text","text":" text"}}""" })
            foreach (var e in reader.Read(Update(line), 1000))
            {
                var rows = converter.Apply(e).Rows;
                if (e is TextChunk { Text: " text" })
                {
                    var row = Assert.Single(rows);
                    Assert.Equal("m", row.ItemId);
                    Assert.Contains("new text", row.Payload.GetRawText());
                    Assert.DoesNotContain("old", row.Payload.GetRawText());
                }
            }
    }

    [Fact]
    public void A_shorter_replacement_and_an_empty_thought_clear_the_old_text()
    {
        var converter = new RowConverter();
        converter.Apply(new TurnStarted(0, "run", "hi"));
        converter.Apply(new TextChunk(1000, "m", "long old text"));
        var replaced = Assert.Single(converter.Apply(new TextChunk(1100, "m", "x", Replace: true)).Rows);
        Assert.Equal("m", replaced.ItemId);
        Assert.DoesNotContain("long old", replaced.Payload.GetRawText());
        converter.Apply(new ThoughtChunk(1200, "t", "thinking"));
        var cleared = Assert.Single(converter.Apply(new ThoughtChunk(1300, "t", "", Replace: true)).Rows);
        Assert.Equal("", cleared.Payload.GetProperty("text").GetString());
    }

    [Fact]
    public void First_tool_call_update_creates_the_call_and_later_patch_merges()
    {
        var reader = new UpdateReader();
        var first = Assert.IsType<ToolCallChanged>(Assert.Single(reader.Read(Update("""{"sessionUpdate":"tool_call_update","toolCallId":"t","title":"Read file","status":"pending"}"""))));
        var next = Assert.IsType<ToolCallChanged>(Assert.Single(reader.Read(Update("""{"sessionUpdate":"tool_call_update","toolCallId":"t","status":"completed"}"""))));
        Assert.True(first.Started); Assert.False(next.Started);
        Assert.Equal("Read file", next.Fields.Title); Assert.Equal("completed", next.Fields.Status);
    }

    [Fact]
    public void Idle_error_is_turn_failed_with_code_and_repeated_idle_is_idempotent()
    {
        var reader = new UpdateReader();
        Assert.IsType<TurnStarted>(Assert.Single(reader.Read(Update("""{"sessionUpdate":"state_update","state":"running"}"""))));
        var idle = Update("""{"sessionUpdate":"state_update","state":"idle","stopReason":"error","error":{"code":-32603,"message":"failed"}}""");
        var failed = Assert.IsType<TurnFailed>(Assert.Single(reader.Read(idle)));
        Assert.Equal("-32603", failed.Code); Assert.Empty(reader.Read(idle));
    }

    [Fact]
    public void Background_list_becomes_task_changes_and_empty_list_ends_them()
    {
        var reader = new UpdateReader();
        var task = Assert.IsType<TaskChanged>(Assert.Single(reader.Read(Update("""{"sessionUpdate":"state_update","state":"idle","background":[{"id":"b","title":"Build","state":"running"}]}"""))));
        Assert.Equal("running", task.State); Assert.False(task.CanStop);
        var ended = Assert.IsType<TaskChanged>(Assert.Single(reader.Read(Update("""{"sessionUpdate":"state_update","state":"idle","background":[]}"""))));
        Assert.Equal("completed", ended.State);
    }

    [Fact]
    public void Observed_codex_v2_chunks_title_usage_and_end_convert()
    {
        // Exact update shapes observed in Task 0, codex-acp 2.5.0 (2026-11-01).
        var reader = new UpdateReader();
        Assert.IsType<SessionMessage>(Assert.Single(reader.Read(Update("""{"messageId":"m","content":{"text":"hi","type":"text"},"sessionUpdate":"user_message_chunk"}"""))));
        Assert.IsType<UsageReported>(Assert.Single(reader.Read(Update("""{"used":14601,"size":475000,"sessionUpdate":"usage_update"}"""))));
        Assert.IsType<TitleChanged>(Assert.Single(reader.Read(Update("""{"title":"hi","sessionUpdate":"session_info_update"}"""))));
        Assert.IsType<TurnEnded>(Assert.Single(reader.Read(Update("""{"stopReason":"end_turn","state":"idle","sessionUpdate":"state_update"}"""))));
    }

    [Fact]
    public void Task_tool_updates_show_subagent_progress_without_creating_an_engine_session()
    {
        var reader = new UpdateReader();
        var events = reader.Read(Update("""{"sessionUpdate":"tool_call_update","toolCallId":"child","name":"Task","title":"Review","status":"in_progress"}"""));
        Assert.Equal("running", Assert.Single(events.OfType<SubagentChanged>()).State);
        var ended = reader.Read(Update("""{"sessionUpdate":"tool_call_update","toolCallId":"child","status":"completed"}"""));
        Assert.Equal("idle", Assert.Single(ended.OfType<SubagentChanged>()).State);
    }
    [Fact]
    public void Official_v2_tool_content_chunks_append_and_item_plans_replace_by_id()
    {
        // Shapes from @agentclientprotocol/sdk 2.2.0 schema/v2/schema.json:
        // ToolCallContentChunk, PlanUpdate, PlanItems.
        var reader = new UpdateReader();
        reader.Read(Update("""{"sessionUpdate":"tool_call_update","toolCallId":"t","name":"Bash","content":[{"type":"content","content":{"type":"text","text":"one"}}]}"""));
        var call = Assert.IsType<ToolCallChanged>(Assert.Single(reader.Read(Update("""{"sessionUpdate":"tool_call_content_chunk","toolCallId":"t","content":{"type":"content","content":{"type":"text","text":" two"}}}"""))));
        Assert.Equal("one two", call.Fields.ContentText);
        reader.Read(Update("""{"sessionUpdate":"plan_update","plan":{"type":"items","planId":"p","entries":[{"content":"Read","priority":"high","status":"pending"}]}}"""));
        var plan = Assert.IsType<PlanChanged>(Assert.Single(reader.Read(Update("""{"sessionUpdate":"plan_update","plan":{"type":"items","planId":"p","entries":[{"content":"Read","priority":"high","status":"completed"}]}}"""))));
        Assert.Equal("completed", Assert.Single(plan.Entries).Status);
    }

    [Fact]
    public void Replacement_after_a_message_closed_updates_its_original_item()
    {
        var converter = new RowConverter();
        converter.Apply(new TurnStarted(0, "r", "hi"));
        converter.Apply(new TextChunk(1, "m", "old"));
        converter.Apply(new TurnEnded(2, "end_turn", null));
        var row = Assert.Single(converter.Apply(new TextChunk(3, "m", "changed", Replace: true)).Rows);
        Assert.Equal(("r", "m", RowKind.Message), (row.RunId, row.ItemId, row.Kind));
        Assert.DoesNotContain("old", row.Payload.GetRawText());
    }

    [Fact]
    public void Different_thought_message_ids_do_not_append_to_the_same_item()
    {
        var converter = new RowConverter();
        converter.Apply(new TurnStarted(0, "r", "hi"));
        var first = Assert.Single(converter.Apply(new ThoughtChunk(1, "t1", "first")).Rows);
        var next = converter.Apply(new ThoughtChunk(2, "t2", "second")).Rows;
        Assert.Equal("t1:thinking", first.ItemId);
        Assert.Equal("t2:thinking", next[^1].ItemId);
        Assert.Equal("second", next[^1].Payload.GetProperty("text").GetString());
    }

}
