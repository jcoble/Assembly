using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Socket;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed class SessionItemWireCodecTests
{
    [Fact]
    public void Small_generated_items_remain_exact_with_only_initial_state_acknowledgement()
    {
        var item = Item("s", "Title\0世界😀");
        var change = Change(item);
        Assert.Equal(change, SessionItemWireCodec.Adapt(change, "engine").ToArray());
        var state = State([item]);
        var actual = Encoding.UTF8.GetString(SessionItemWireCodec.Adapt(state, "engine").Span);
        Assert.Equal(Encoding.UTF8.GetString(state)[..^1] + ",\"sessionTitleParts\":1}", actual);
        using var parsed = JsonDocument.Parse(actual);
        Assert.Equal(JsonSerializer.Serialize(item, SessionsJson.Default.SessionItem),
            parsed.RootElement.GetProperty("items")[0].GetProperty("item").GetRawText());
    }

    [Fact]
    public void Six_individually_fitting_titles_become_one_complete_bounded_state_in_original_order()
    {
        var items = Enumerable.Range(0, 6).Select(i => Item("s" + i, new string('x', 3 * 1024 * 1024) + i)).ToArray();
        var original = State(items);
        Assert.True(original.Length > 16 * 1024 * 1024);
        Assert.All(items, item => Assert.True(Change(item).Length < SocketConnection.MaxFrame));
        var adapted = SessionItemWireCodec.Adapt(original, "engine");
        Assert.True(adapted.Length <= SocketConnection.MaxFrame);
        using var parsed = JsonDocument.Parse(adapted);
        Assert.Equal("state", parsed.RootElement.GetProperty("t").GetString());
        Assert.Equal(1, parsed.RootElement.GetProperty("sessionTitleParts").GetInt32());
        var entries = parsed.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(items.Select(item => item.SessionId), entries.Select(entry => entry.GetProperty("key").GetString()));
        for (var i = 0; i < 5; i++)
        {
            var notice = entries[i].GetProperty("item");
            Assert.False(notice.TryGetProperty("title", out _));
            Assert.Equal(1, notice.GetProperty("sessionTitleRefVersion").GetInt32());
            var reference = notice.GetProperty("titleReference").Deserialize(SessionsJson.Default.LegacySessionTitleRef)!;
            Assert.Equal(items[i].SessionId, reference.SessionId);
            Assert.Equal("engine", reference.PublisherEngineMachine);
            Assert.Equal(SessionTitleHash.Compute(Encoding.UTF8.GetBytes(items[i].Title!)), reference.Sha256);
            AssertMetadata(items[i], notice);
        }
        Assert.Equal(JsonSerializer.Serialize(items[5], SessionsJson.Default.SessionItem), entries[5].GetProperty("item").GetRawText());
    }

    [Fact]
    public void Oversized_change_references_full_strict_bytes_and_preserves_nullable_provenance()
    {
        var item = Item("s", new string('x', 9000000) + "\0😀tail");
        var original = Change(item);
        Assert.True(original.Length > SocketConnection.MaxFrame);
        var adapted = SessionItemWireCodec.Adapt(original, "engine");
        Assert.True(adapted.Length < SocketConnection.MaxFrame);
        using var parsed = JsonDocument.Parse(adapted);
        var notice = parsed.RootElement.GetProperty("item");
        Assert.False(notice.TryGetProperty("title", out _)); AssertMetadata(item, notice);
        var reference = notice.GetProperty("titleReference").Deserialize(SessionsJson.Default.LegacySessionTitleRef)!;
        Assert.Equal(Encoding.UTF8.GetByteCount(item.Title!), reference.Utf8Bytes);
        Assert.Null(reference.StoredMachine); Assert.Null(reference.ParentSessionId); Assert.Null(reference.TitleSource);
        Assert.Equal(item.UpdatedAt, reference.UpdatedAt);
        Assert.Equal(SessionTitleHash.Compute(Encoding.UTF8.GetBytes(item.Title!)), reference.Sha256);
    }

    [Fact]
    public void Null_titles_stay_inline_and_excess_non_title_metadata_fails_without_partial_state()
    {
        var small = Item("s", null);
        using var parsed = JsonDocument.Parse(SessionItemWireCodec.Adapt(State([small]), "engine"));
        var item = parsed.RootElement.GetProperty("items")[0].GetProperty("item");
        Assert.Equal(JsonValueKind.Null, item.GetProperty("title").ValueKind);
        Assert.False(item.TryGetProperty("sessionTitleRefVersion", out _));
        var error = Assert.Throws<CallFailed>(() => SessionItemWireCodec.Adapt(State([small with { RepoUrl = new string('r', 5 * 1024 * 1024) }]), "engine"));
        Assert.Contains("non-title", error.Message);
    }

    private static void AssertMetadata(SessionItem original, JsonElement actual)
    {
        var expected = JsonSerializer.SerializeToElement(original, SessionsJson.Default.SessionItem);
        foreach (var property in expected.EnumerateObject().Where(property => property.Name != "title"))
            Assert.Equal(property.Value.GetRawText(), actual.GetProperty(property.Name).GetRawText());
    }
    private static SessionItem Item(string id, string? title) => new(id, "codex", "repo", title, null, null, null, 123,
        "idle", null, false, 0, false, false, null, null, null);
    private static byte[] State(IReadOnlyList<SessionItem> items) => Encoding.UTF8.GetBytes("{\"t\":\"state\",\"sub\":9223372036854775807,\"items\":[" +
        string.Join(",", items.Select(item => "{\"key\":\"" + item.SessionId + "\",\"item\":" + JsonSerializer.Serialize(item, SessionsJson.Default.SessionItem) + "}")) + "]}");
    private static byte[] Change(SessionItem item) => Encoding.UTF8.GetBytes("{\"t\":\"change\",\"sub\":9223372036854775807,\"op\":\"upsert\",\"key\":\"" +
        item.SessionId + "\",\"item\":" + JsonSerializer.Serialize(item, SessionsJson.Default.SessionItem) + "}");
}
