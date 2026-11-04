using System.Text.Json;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.V2;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests.Acp;

public sealed class UpdateReaderTests
{
    private static SessionNotification Update(string json) => new("s", JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void Context_usage_preserves_nullable_token_counts_from_the_same_typed_report()
    {
        var reader = new UpdateReader();
        reader.Read(Update("""{"sessionUpdate":"state_update","state":"running"}"""));
        var events = reader.Read(Update("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn","usage":{"inputTokens":123,"outputTokens":null,"used":800,"size":10000}}"""));
        var report = Assert.Single(events.OfType<UsageReported>());
        Assert.Equal(123, report.InputTokens); Assert.Null(report.OutputTokens);
        Assert.Equal(800, report.Used); Assert.Equal(10000, report.Size);
        var converter = new RowConverter(); converter.Apply(new TurnStarted(0, "run", "prompt"));
        var context = Assert.Single(converter.Apply(report).Rows, row => row.Kind == RowKind.Context);
        Assert.Equal(123, context.Payload.GetProperty("inputTokens").GetInt64());
        Assert.False(context.Payload.TryGetProperty("outputTokens", out _));
    }

    [Fact]
    public void Standalone_context_report_does_not_invent_token_breakdowns()
    {
        var report = Assert.IsType<UsageReported>(Assert.Single(new UpdateReader().Read(Update(
            """{"used":14601,"size":475000,"sessionUpdate":"usage_update"}"""))));
        Assert.Null(report.InputTokens); Assert.Null(report.OutputTokens);
        Assert.Equal(14601, report.Used); Assert.Equal(475000, report.Size);
    }


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
    public void Known_in_progress_tool_is_retained_at_idle_and_only_its_status_settles_it()
    {
        var reader = new UpdateReader();
        reader.Read(Update("""{"sessionUpdate":"tool_call_update","toolCallId":"b","title":"Build","status":"in_progress"}"""));
        var task = Assert.Single(reader.Read(Update("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""")).OfType<TaskChanged>());
        Assert.Equal("running", task.State); Assert.False(task.CanStop);
        Assert.Empty(reader.Read(Update("""{"sessionUpdate":"state_update","state":"unknown","background":[]}""")).OfType<TaskChanged>());
        var ended = Assert.Single(reader.Read(Update("""{"sessionUpdate":"tool_call_update","toolCallId":"b","status":"completed"}""")).OfType<TaskChanged>());
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
    public void Task_tool_remains_a_tool_without_inventing_a_native_child_id()
    {
        var events = new UpdateReader().Read(Update("""{"sessionUpdate":"tool_call_update","toolCallId":"tool","name":"Task","status":"in_progress"}"""));
        Assert.IsType<ToolCallChanged>(Assert.Single(events));
    }

    [Fact]
    public void Native_subagent_patches_preserve_omission_clear_null_and_replace_state_snapshots()
    {
        var reader = new UpdateReader();
        SubagentChanged Read(string fields) => Assert.IsType<SubagentChanged>(Assert.Single(reader.Read(Update(
            "{\"sessionUpdate\":\"subagent_update\",\"sessionId\":\"native\"," + fields + "}"))));
        var unknown = Read("\"title\":\"Review\",\"description\":\"Role\",\"capabilities\":{\"cancel\":{}}");
        Assert.Equal("unknown", unknown.State);
        Assert.True(unknown.CanCancel);
        var running = Read("\"state\":{\"state\":\"running\"}");
        Assert.Equal("Review", running.Title);
        var idle = Read("\"state\":{\"state\":\"idle\",\"stopReason\":\"error\",\"error\":{\"code\":-32603,\"message\":\"failed\"}}");
        Assert.Equal("failed", idle.Error);
        var replaced = Read("\"state\":{\"state\":\"running\"},\"capabilities\":{}");
        Assert.Null(replaced.StopReason);
        Assert.Null(replaced.Error);
        Assert.False(replaced.CanCancel);
        var cleared = Read("\"state\":null,\"title\":null,\"description\":null,\"capabilities\":null");
        Assert.Equal("unknown", cleared.State);
        Assert.Null(cleared.Title);
        Assert.Null(cleared.Description);
        Assert.False(cleared.CanCancel);
        var omitted = Read("\"capabilities\":{\"cancel\":null}");
        Assert.Equal(cleared with { At = omitted.At }, omitted);
    }

    [Fact]
    public void Provider_messages_resolve_sparse_participants_and_content_without_becoming_user_acknowledgements()
    {
        var reader = new UpdateReader();
        ProviderSessionMessage Read(string fields, bool chunk = false) => Assert.IsType<ProviderSessionMessage>(Assert.Single(reader.Read(Update(
            "{\"sessionUpdate\":\"" + (chunk ? "session_message_chunk" : "session_message") + "\",\"messageId\":\"native-message\"," + fields + "}"))));
        var first = Read("\"content\":{\"type\":\"text\",\"text\":\"one\"}", chunk: true);
        Assert.Null(first.SenderSessionId);
        var linked = Read("\"senderSessionId\":\"parent\",\"recipientSessionId\":\"child\",\"senderMessageId\":\"outgoing\",\"recipientMessageId\":\"incoming\"");
        Assert.Equal("one", Assert.Single(linked.Content).GetProperty("text").GetString());
        var chunked = Read("\"content\":{\"type\":\"text\",\"text\":\"two\"}", chunk: true);
        Assert.Equal(2, chunked.Content.Count);
        Assert.Single(first.Content); // Later chunks cannot mutate earlier event snapshots.
        var replaced = Read("\"content\":[{\"type\":\"text\",\"text\":\"replacement\"}],\"senderMessageId\":null");
        Assert.Equal("replacement", Assert.Single(replaced.Content).GetProperty("text").GetString());
        Assert.Equal("parent", replaced.SenderSessionId);
        Assert.Equal("child", replaced.RecipientSessionId);
        Assert.Null(replaced.SenderMessageId);
        Assert.Equal("incoming", replaced.RecipientMessageId);
        Assert.Empty(Read("\"content\":null,\"senderSessionId\":null,\"recipientSessionId\":null,\"recipientMessageId\":null").Content);
        Assert.Empty(Read("\"content\":[]").Content);
        Assert.IsType<SessionMessage>(Assert.Single(reader.Read(Update("""{"sessionUpdate":"user_message","messageId":"ack","content":[]}"""))));
    }

    [Fact]
    public void Genuine_Claude_native_message_provenance_survives_replay_separately_from_prompt_ack()
    {
        var events = Assembly.Tests.Fixtures.FixtureReplay.Events("acp-v2/claude-native-subagents.jsonl");
        var messages = events.OfType<ProviderSessionMessage>().ToList();
        Assert.Equal(3, messages.Count);
        Assert.Empty(events.OfType<SessionMessage>());
        Assert.Equal(messages[0].MessageId, messages[1].MessageId);
        Assert.Equal(messages[0].MessageId, messages[0].RecipientMessageId);
        Assert.Null(messages[1].RecipientMessageId);
        Assert.NotEqual(messages[0].SessionId, messages[1].SessionId);
        Assert.NotNull(messages[1].SenderMessageId);
        var child = Assert.Single(events.OfType<SubagentChanged>().Select(e => e.AcpSessionId).Distinct());
        Assert.Equal(child, messages[0].RecipientSessionId);
        Assert.Equal(child, messages[2].SenderSessionId);
        Assert.Equal(2, messages[2].Content.Count);
        Assert.Empty(new RowConverter().Apply(messages[0]).Rows);
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

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void Tool_content_chunks_keep_both_diffs_and_replacement_clears_saved_detail(string clear)
    {
        // Synthetic regression using the SDK 2.2.0 ToolCallContentChunk/ToolCallUpdate shapes.
        using var file = new TempDatabase();
        using (var db = TestDatabase.Open(file.Path))
        {
            var store = RowStore.Open(db);
            var converter = new RowConverter();
            var reader = new UpdateReader();
            ToolCallChanged Apply(string json)
            {
                var change = Assert.IsType<ToolCallChanged>(Assert.Single(reader.Read(Update(json))));
                store.Commit("s", converter.Apply(change));
                return change;
            }
            Apply("""{"sessionUpdate":"tool_call_update","toolCallId":"t","status":"in_progress"}""");
            Apply("""{"sessionUpdate":"tool_call_content_chunk","toolCallId":"t","content":{"type":"diff","changes":[{"operation":"modify","path":"a.txt"}],"patch":{"format":"git_patch","text":"diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-old\n+new\n"}}}""");
            var both = Apply("""{"sessionUpdate":"tool_call_content_chunk","toolCallId":"t","content":{"type":"diff","changes":[{"operation":"modify","path":"b.txt"}],"patch":{"format":"git_patch","text":"diff --git a/b.txt b/b.txt\n--- a/b.txt\n+++ b/b.txt\n@@ -1 +1 @@\n-old\n+new\n"}}}""");
            Assert.Equal(new[] { "a.txt", "b.txt" }, both.Fields.Diffs!.SelectMany(diff => diff.Paths));
            var detail = store.ToolCall("s", "t")!;
            Assert.Contains("a.txt", detail.Diff);
            Assert.Contains("b.txt", detail.Diff);
            var line = Assert.Single(store.PageBefore("s", long.MaxValue, 10).Rows);
            Assert.Equal(2, line.Payload.GetProperty("added").GetInt32());
            Assert.Equal(2, line.Payload.GetProperty("removed").GetInt32());
            Apply("""{"sessionUpdate":"tool_call_update","toolCallId":"t","title":"omitted"}""");
            Assert.Contains("a.txt", store.ToolCall("s", "t")!.Diff);
            Apply("{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"t\",\"content\":" + clear + "}");
            Apply("""{"sessionUpdate":"tool_call_update","toolCallId":"t","status":"completed"}""");
        }
        using var reopened = TestDatabase.Open(file.Path);
        var saved = RowStore.Open(reopened);
        Assert.Null(saved.ToolCall("s", "t")!.Diff);
        Assert.Null(saved.ToolCall("s", "t")!.Path);
        var row = Assert.Single(saved.PageBefore("s", long.MaxValue, 10).Rows);
        Assert.False(row.Payload.GetProperty("hasDetail").GetBoolean());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void Omitted_tool_content_preserves_text_but_explicit_empty_replacement_clears_it(string clear)
    {
        var reader = new UpdateReader();
        reader.Read(Update("""{"sessionUpdate":"tool_call_content_chunk","toolCallId":"t","content":{"type":"content","content":{"type":"text","text":"old"}}}"""));
        var kept = Assert.IsType<ToolCallChanged>(Assert.Single(reader.Read(Update("""{"sessionUpdate":"tool_call_update","toolCallId":"t","title":"omitted"}"""))));
        Assert.Equal("old", kept.Fields.ContentText);
        var cleared = Assert.IsType<ToolCallChanged>(Assert.Single(reader.Read(Update("{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"t\",\"content\":" + clear + "}"))));
        Assert.True(string.IsNullOrEmpty(cleared.Fields.ContentText));
        var appended = Assert.IsType<ToolCallChanged>(Assert.Single(reader.Read(Update("""{"sessionUpdate":"tool_call_content_chunk","toolCallId":"t","content":{"type":"content","content":{"type":"text","text":"new"}}}"""))));
        Assert.Equal("new", appended.Fields.ContentText);
    }
}
