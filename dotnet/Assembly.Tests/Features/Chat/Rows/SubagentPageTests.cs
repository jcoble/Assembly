using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Engine.Sessions;
using Assembly.Tests.Engine;

namespace Assembly.Tests.Features.Chat.Rows;

public sealed class SubagentPageTests
{
    [Fact]
    public void Latest_native_rows_are_paged_by_immutable_card_id_in_binary_order()
    {
        using var fixture = new EngineFixture(); var store = RowStore.Open(fixture.Db);
        Add(store, "parent", "a", "running"); Add(store, "parent", "B", "running"); Add(store, "parent", "a", "idle");
        var first = store.Subagents("parent", null, 1, default);
        Assert.Equal("B", Assert.Single(first.Items).ItemId); Assert.NotNull(first.Next); Assert.NotEqual("B", first.Next);
        var second = store.Subagents("parent", first.Next, 1, default);
        var card = Assert.Single(second.Items);
        Assert.Equal(("a", 3L, "parent", "idle"), (card.ItemId, card.Seq, card.SessionId, card.State));
        Assert.Null(second.Next);
        Add(store, "parent", "😀", "running"); Add(store, "parent", "\uE000", "running");
        Assert.Equal(["B", "a", "\uE000", "😀"], store.Subagents("parent", null, 4, default).Items.Select(item => item.ItemId));
        Assert.Equal(0, fixture.Number("SELECT COUNT(*) FROM engine_session"));
    }

    [Fact]
    public void Other_sessions_engine_sessions_and_latest_helper_cards_are_not_native_activity()
    {
        using var fixture = new EngineFixture(); var store = RowStore.Open(fixture.Db);
        Assert.True(fixture.Apply(new StartSession("codex", null, "/work", "Engine work", null, null, null), "engine-work").Applied);
        Add(store, "other", "a", "running"); Add(store, "parent", "same", "running");
        Add(store, "parent", "same", "idle", helper: true); // A latest helper cannot expose an older native version.
        var before = fixture.Number("SELECT COUNT(*) FROM engine_session");
        var page = store.Subagents("parent", null, 50, default);
        Assert.Empty(page.Items); Assert.Null(page.Next);
        Assert.Equal(before, fixture.Number("SELECT COUNT(*) FROM engine_session"));
    }

    [Fact]
    public void Updates_during_paging_keep_one_cut_before_new_versions_helper_flags_and_cards()
    {
        using var fixture = new EngineFixture(); var store = RowStore.Open(fixture.Db);
        Add(store, "p", "a", "running"); Add(store, "p", "b", "running");
        var first = store.Subagents("p", null, 1, default);
        Add(store, "p", "a", "idle"); Add(store, "p", "b", "idle", helper: true); Add(store, "p", "c", "running");
        var next = store.Subagents("p", first.Next, 1, default);
        Assert.Equal(("b", 2L, "running"), (next.Items[0].ItemId, next.Items[0].Seq, next.Items[0].State));
        Assert.Null(next.Next); Assert.Equal("a", Assert.Single(first.Items).ItemId);
        var refreshed = store.Subagents("p", null, 50, default);
        Assert.Equal([("a", 3L, "idle"), ("c", 5L, "running")], refreshed.Items.Select(item => (item.ItemId, item.Seq, item.State)));
    }

    [Fact]
    public void Continuations_refuse_different_parent_source_generation_route_and_unavailable_cut()
    {
        using var fixture = new EngineFixture(); var store = RowStore.Open(fixture.Db);
        Add(store, "p", "a", "running"); Add(store, "p", "b", "idle");
        var source = new ExpectedSource(fixture.Machine, 0, null);
        var first = store.Subagents("p", null, 1, default, source);
        Assert.Equal("bad_args", Assert.Throws<CallFailed>(() => store.Subagents("other", first.Next, 1, default, source)).Reason);
        foreach (var changed in new[] { source with { Machine = "other" }, source with { Generation = 1 }, source with { RouteMachine = "other" } })
            Assert.Equal("source_changed", Assert.Throws<CallFailed>(() => store.Subagents("p", first.Next, 1, default, changed)).Reason);
        Assert.Equal("source_changed", Assert.Throws<CallFailed>(() => store.Subagents("p", first.Next, 1, default)).Reason);
        Assert.Equal("bad_args", Assert.Throws<CallFailed>(() => store.Subagents("p", "a", 1, default, source)).Reason);
        var future = SubagentCursor.Write("p", source, store.LastSeq("p") + 1, "a");
        Assert.Equal("source_changed", Assert.Throws<CallFailed>(() => store.Subagents("p", future, 1, default, source)).Reason);
        var owner = new ExpectedSource(source.Machine, 0, null);
        var desktop = new ExpectedSource(source.Machine, 7, "workbox-test");
        var routed = SubagentCursor.Rebind(first.Next, "p", owner, desktop);
        Assert.Equal(first.Next, SubagentCursor.Rebind(routed, "p", desktop, owner));
        Assert.Equal("source_changed", Assert.Throws<CallFailed>(() => SubagentCursor.Rebind(routed, "p", desktop with { Generation = 8 }, owner)).Reason);
        Assert.Equal(0, fixture.Number("SELECT COUNT(*) FROM engine_session"));
    }

    [Fact]
    public void Default_and_maximum_limits_are_bounded_and_empty_reads_have_no_cursor()
    {
        using var fixture = new EngineFixture(); var store = RowStore.Open(fixture.Db);
        Assert.Empty(store.Subagents("absent", null, 50, default).Items);
        Assert.Null(store.Subagents("absent", null, 50, default).Next);
        for (var i = 0; i < 250; i++) Add(store, "p", i.ToString("D3"), "running");
        var args = JsonSerializer.Deserialize("{\"session\":\"p\"}", RowJsonContext.Rows.SubagentArgs)!;
        Assert.Equal(50, args.Limit);
        Assert.Equal(50, store.Subagents(args.Session, args.After, args.Limit, default).Items.Count);
        Assert.Equal(200, store.Subagents("p", null, 1000, default).Items.Count);
    }

    [Fact]
    public void Full_serialized_pages_include_cursor_escaping_and_advance_without_losing_cards()
    {
        using var fixture = new EngineFixture(); var store = RowStore.Open(fixture.Db);
        var text = new string('x', SubagentPage.PreviewBytes);
        for (var i = 0; i < 90; i++) Add(store, "p", i.ToString("D3") + "\"\\", "idle", title: text, description: text);
        List<string> seen = []; string? after = null;
        do
        {
            var page = store.Subagents("p", after, 200, default);
            Assert.NotEmpty(page.Items);
            Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(page, RowJsonContext.Rows.SubagentPage).Length, 1, SubagentPage.MaxBytes);
            foreach (var item in page.Items)
                Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(item, RowJsonContext.Rows.SubagentDisplay).Length, 1, SubagentPage.ItemMaxBytes);
            if (page.Next is not null) Assert.NotEqual(page.Items[^1].ItemId, page.Next);
            Assert.NotEqual(after, page.Next ?? "finished");
            seen.AddRange(page.Items.Select(item => item.ItemId)); after = page.Next;
        } while (after is not null);
        Assert.Equal(90, seen.Count); Assert.Equal(90, seen.Distinct().Count());
    }

    [Fact]
    public void Giant_source_text_is_previewed_at_complete_utf8_boundaries_without_losing_nul()
    {
        using var fixture = new EngineFixture(); var store = RowStore.Open(fixture.Db);
        var title = new string('a', SubagentPage.PreviewBytes - 1) + "😀tail";
        var description = "\0€" + new string('b', 2 * 1024 * 1024);
        Add(store, "p", "native", "idle", title: title, description: description, error: new string('c', 9000));
        var card = Assert.Single(store.Subagents("p", null, 50, default).Items);
        Assert.Equal(new string('a', SubagentPage.PreviewBytes - 1), card.Title);
        Assert.StartsWith("\0€", card.Description!);
        Assert.Equal(SubagentPage.PreviewBytes, Encoding.UTF8.GetByteCount(card.Description!));
        Assert.Equal(SubagentPage.PreviewBytes, Encoding.UTF8.GetByteCount(card.Error!));
        Assert.DoesNotContain('�', card.Title + card.Description);
        Assert.Equal(description, store.RowsAfter("p", 0)[0].Payload.GetProperty("description").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Oversized_identity_or_serialized_escaping_refuses_without_empty_continuations(bool escaping)
    {
        using var fixture = new EngineFixture(); var store = RowStore.Open(fixture.Db);
        Add(store, "p", "a", "idle");
        Add(store, "p", escaping ? "z" : new string('z', 40 * 1024), escaping ? new string('\0', 6000) : "idle");
        var first = store.Subagents("p", null, 1, default);
        Assert.Equal("a", Assert.Single(first.Items).ItemId); Assert.NotNull(first.Next);
        Assert.Equal("subagent_too_large", Assert.Throws<CallFailed>(() => store.Subagents("p", first.Next, 1, default)).Reason);
    }

    [Fact]
    public void Cancelled_reads_stop_and_replicated_parent_records_return_the_same_projection()
    {
        using var owner = new EngineFixture(); using var replica = new EngineFixture();
        var source = RowStore.Open(owner.Db); var copy = RowStore.Open(replica.Db);
        Add(source, "p", "a", "running"); Add(source, "p", "a", "idle", description: "native detail");
        Add(source, "p", "b", "running");
        foreach (var row in source.RowsAfter("p", 0)) Assert.True(copy.Copy(row));
        var expected = source.Subagents("p", null, 1, default); var actual = copy.Subagents("p", null, 1, default);
        Assert.Equal(expected.Items, actual.Items); Assert.Equal(expected.Next, actual.Next);
        Add(source, "p", "b", "idle");
        foreach (var row in source.RowsAfter("p", copy.LastSeq("p"))) Assert.True(copy.Copy(row));
        var ownerNext = source.Subagents("p", expected.Next, 1, default);
        var replicaNext = copy.Subagents("p", actual.Next, 1, default);
        Assert.Equal(ownerNext.Items, replicaNext.Items); Assert.Equal(ownerNext.Next, replicaNext.Next);
        Assert.Equal(("b", 3L, "running"), (replicaNext.Items[0].ItemId, replicaNext.Items[0].Seq, replicaNext.Items[0].State));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => copy.Subagents("p", null, 50, cancelled.Token));
        Assert.Equal(0, owner.Number("SELECT COUNT(*) FROM engine_session"));
        Assert.Equal(0, replica.Number("SELECT COUNT(*) FROM engine_session"));
    }

    private static void Add(RowStore store, string parent, string item, string state, bool? helper = null,
        string title = "Native title", string? description = null, string? error = null)
    {
        var payload = new SubagentPayload(parent, title, description, state, null, error, false, "launch-tool", Helper: helper);
        var batch = new RowBatch();
        batch.Rows.Add(new NewRow(null, item, RowKind.Subagent, JsonSerializer.SerializeToElement(payload, RowJsonContext.Rows.SubagentPayload), 1000));
        store.Commit(parent, batch);
    }
}
