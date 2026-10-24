using System.Text.Json;
using Assembly.Engine.Markdown;
using Assembly.Engine.Rows;
using Assembly.Tests.Fixtures;
using static Assembly.Tests.Rows.Converted;

namespace Assembly.Tests.Rows;

/// <summary>The recorded text turn: one streamed answer with markdown, and the turn's stats.</summary>
public sealed class TextTurnTests
{
    private const string MessageId = "msg_011CgLZJZT1Cd5h4Vt1LfGZ3";
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
        // The 95 chunks arrived in 37 windows of 100 ms, so there can be no more deltas than that.
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
        var answer = string.Concat(FixtureReplay.Events("acp/text.jsonl").OfType<Assembly.Engine.Events.TextChunk>().Select(chunk => chunk.Text));
        Assert.Equal(1590, answer.Length);
        AssertSameJson(SafeMarkdown.ToJson(SafeMarkdown.Parse(answer)), Get(Turn.Last(RowKind.Message), "blocks").GetRawText());
    }

    [Fact]
    public void The_turn_ends_with_stats_then_finished()
    {
        Assert.Equal([RowKind.Stats, RowKind.Turn], Turn.Rows.TakeLast(2).Select(row => row.Kind));
        Assert.Equal("finished", Text(Turn.Rows[^1], "state"));

        var stats = Turn.Last(RowKind.Stats).Payload;
        var started = FixtureReplay.Ms("2026-10-24T06:04:46.5436256+00:00");
        Assert.Equal("run-1", stats.GetProperty("runId").GetString());
        Assert.Equal("completed", stats.GetProperty("status").GetString());
        Assert.Equal(started, stats.GetProperty("startedAt").GetInt64());
        Assert.Equal(FixtureReplay.Ms("2026-10-24T06:04:53.9479665+00:00") - started, stats.GetProperty("durationMs").GetInt64());
        Assert.Equal(FixtureReplay.Ms("2026-10-24T06:04:49.2117303+00:00") - started, stats.GetProperty("firstTokenMs").GetInt64());
        Assert.Equal("claude-opus-5-5", stats.GetProperty("model").GetString());
        Assert.Equal(2, stats.GetProperty("inputTokens").GetInt64());
        Assert.Equal(641, stats.GetProperty("outputTokens").GetInt64());
        // 10,836 cached reads of 2 + 10,836 + 9,977 input tokens.
        Assert.Equal(52.1, stats.GetProperty("cacheHitPct").GetDouble());
        Assert.Equal(0.0949418m, stats.GetProperty("costUsd").GetDecimal());
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
