using System.Text.Json;
using Assembly.Application.Features.Chat.Rows.Markdown;
using Assembly.Domain.Markdown;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Domain.Events;
using Assembly.Tests.Fixtures;
using static Assembly.Tests.Features.Chat.Rows.Converted;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>The recorded text turn: one streamed answer with markdown, and the turn's stats.</summary>
public sealed class TextTurnTests
{
    private const string MessageId = "msg_011CghqFzWEcFUT5eAjXykpb";
    private static readonly Converted Turn = Fixture("acp/text.jsonl");

    [Fact]
    public void The_turn_opens_with_the_prompt_then_started()
    {
        Assert.Equal([RowKind.User, RowKind.Turn], Turn.Rows.Take(2).Select(row => row.Kind));
        Assert.StartsWith("Explain what a mutex is", Text(Turn.Rows[0], "text"));
        Assert.Equal("started", Text(Turn.Rows[1], "state"));
        Assert.All(Turn.Rows, row => Assert.Equal("run-1", row.RunId));
    }

    [Fact]
    public void The_answer_streams_as_deltas_then_ends_as_one_message()
    {
        var item = Turn.OfItem(MessageId);
        Assert.Equal(RowKind.Message, item[^1].Kind);
        Assert.All(item.SkipLast(1), row => Assert.Equal(RowKind.Delta, row.Kind));
        Assert.Single(Turn.OfKind(RowKind.Message));
    }

    [Fact]
    public void Deltas_are_coalesced_to_one_per_100_ms_window()
    {
        // Bound the saved deltas for this capture and preserve the 100 ms spacing contract.
        var deltas = Turn.OfKind(RowKind.Delta);
        Assert.InRange(deltas.Count, 2, 37);
        Assert.All(deltas.Zip(deltas.Skip(1)), pair => Assert.True(pair.Second.CreatedAt - pair.First.CreatedAt >= RowConverter.CoalesceMs));
    }

    [Fact]
    public void Applying_the_deltas_in_order_draws_the_text_they_carry()
    {
        var shown = new List<JsonElement>();
        var text = "";
        foreach (var delta in Turn.OfKind(RowKind.Delta))
        {
            var from = Get(delta, "from").GetInt32();
            Assert.InRange(from, 0, shown.Count);
            shown = [.. shown.Take(from), .. Get(delta, "blocks").EnumerateArray()];
            text += Text(delta, "text");
            AssertSameJson(SafeMarkdown.ToJson(SafeMarkdown.Parse(text)), ArrayJson(shown));
        }
    }

    [Fact]
    public void The_message_holds_the_blocks_of_the_whole_answer()
    {
        var answer = string.Concat(FixtureReplay.Events("acp/text.jsonl").OfType<Assembly.Domain.Events.TextChunk>().Select(chunk => chunk.Text));
        Assert.Equal(1264, answer.Length);
        Assert.Equal(answer, Text(Turn.Last(RowKind.Message), "text"));
        AssertSameJson(SafeMarkdown.ToJson(SafeMarkdown.Parse(answer)), Get(Turn.Last(RowKind.Message), "blocks").GetRawText());
    }

    [Fact]
    public void Supplied_user_item_identity_and_ordered_attachment_ids_survive_event_replay_without_changing_delivery_keys()
    {
        AgentEvent[] events = [new TurnStarted(0, "run", "prompt", "client", UserItemId: "actual-user-item", AttachmentIds: ["second", "first"]),
            new TurnSteered(1, "run", "more", "steer-client", UserItemId: "actual-steer-item", AttachmentIds: ["third", "second"])];
        var users = From(events).OfKind(RowKind.User);
        Assert.All(users, row => Assert.Null(row.ItemId));
        Assert.Equal(["actual-user-item", "actual-steer-item"], users.Select(row => Text(row, "itemId")));
        Assert.Equal(["second", "first"], Get(users[0], "attachmentIds").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["third", "second"], Get(users[1], "attachmentIds").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(users.Select(Json), From(events).OfKind(RowKind.User).Select(Json));
        var legacy = JsonSerializer.Deserialize("""{"text":"old"}""", RowJsonContext.Rows.UserPayload)!;
        Assert.Null(legacy.ItemId); Assert.Null(legacy.AttachmentIds);
        Assert.Null(JsonSerializer.Deserialize("""{"blocks":[]}""", RowJsonContext.Rows.MessagePayload)!.Text);
    }

    [Fact]
    public void Direct_message_replacement_preserves_its_exact_markdown_and_clears_obsolete_segment_text()
    {
        const string markdown = "## Exact source\n\n[link](https://example.invalid/path) & `code`\n";
        var rows = From([new TurnStarted(0, "run", "prompt"), new TextChunk(1, "message", "old"),
            new ToolCallChanged(2, "tool", true, new ToolCallFields(Name: "tool")),
            new TextChunk(3, "message", "second segment"), new TextChunk(4, "message", markdown, Replace: true)])
            .OfKind(RowKind.Message);
        Assert.Equal(markdown, Text(rows[^1], "text"));
        Assert.Equal("", Text(rows[^2], "text"));
        AssertSameJson(SafeMarkdown.ToJson(SafeMarkdown.Parse(markdown)), Get(rows[^1], "blocks").GetRawText());
    }

    [Fact]
    public void Finished_message_preserves_exact_original_markdown_with_unchanged_blocks()
    {
        const string markdown = "## Exact source\n\n[link](https://example.invalid/path) & `code`\n";
        var row = From([new TurnStarted(0, "run", "prompt"), new TextChunk(1, "message", markdown), new TurnEnded(2, "end_turn", null)]).Last(RowKind.Message);
        Assert.Equal(markdown, Text(row, "text"));
        AssertSameJson(SafeMarkdown.ToJson(SafeMarkdown.Parse(markdown)), Get(row, "blocks").GetRawText());
    }

    [Fact]
    public void The_turn_ends_with_stats_then_finished()
    {
        Assert.Equal([RowKind.Stats, RowKind.Turn], Turn.Rows.TakeLast(2).Select(row => row.Kind));
        Assert.Equal("finished", Text(Turn.Rows[^1], "state"));

        var stats = Turn.Last(RowKind.Stats).Payload;
        var started = FixtureReplay.Ms("2026-11-04T11:46:29.0467979+00:00");
        Assert.Equal("run-1", stats.GetProperty("runId").GetString());
        Assert.Equal("completed", stats.GetProperty("status").GetString());
        Assert.Equal(started, stats.GetProperty("startedAt").GetInt64());
        Assert.Equal(FixtureReplay.Ms("2026-11-04T11:46:36.9537741+00:00") - started, stats.GetProperty("durationMs").GetInt64());
        Assert.Equal(FixtureReplay.Ms("2026-11-04T11:46:32.8187142+00:00") - started, stats.GetProperty("firstTokenMs").GetInt64());
        Assert.Equal("claude-opus-5-5", stats.GetProperty("model").GetString());
        Assert.Equal(2, stats.GetProperty("inputTokens").GetInt64());
        Assert.Equal(715, stats.GetProperty("outputTokens").GetInt64());
        // 12,932 cached reads of 2 + 12,932 + 19,460 input tokens.
        Assert.Equal(39.9, stats.GetProperty("cacheHitPct").GetDouble());
        Assert.Equal(0.172705m, stats.GetProperty("costUsd").GetDecimal());
        Assert.Equal(0, stats.GetProperty("toolCalls").GetInt32());
        Assert.Equal(JsonValueKind.Null, stats.GetProperty("branch").ValueKind);
    }

    [Fact]
    public void Converting_the_same_events_again_gives_the_same_rows()
    {
        var again = Fixture("acp/text.jsonl");
        Assert.Equal(Turn.Rows.Select(Json), again.Rows.Select(Json));
    }

    internal static string Json(NewRow row) => $"{row.RunId}|{row.ItemId}|{row.Kind}|{row.CreatedAt}|{row.Payload.GetRawText()}";

    internal static string ArrayJson(IEnumerable<JsonElement> blocks) => $"[{string.Join(",", blocks.Select(block => block.GetRawText()))}]";

    internal static void AssertSameJson(string expected, string actual)
    {
        using var left = JsonDocument.Parse(expected);
        using var right = JsonDocument.Parse(actual);
        Assert.True(JsonElement.DeepEquals(left.RootElement, right.RootElement), $"Expected {expected}\nActual   {actual}");
    }
}
