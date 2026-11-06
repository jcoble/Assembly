using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;

namespace Assembly.Tests.Features.Chat.Rows;

public sealed class RawToolDetailTests
{
    [Fact]
    public void Prefixes_keep_exact_original_UTF8_counts_complete_scalars_and_uncapped_fields()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var store = RowStore.Open(db);
        var output = new string('a', 2 * RowsFeature.ToolCap) + "€";
        var diff = new string('b', RowsFeature.ToolCap - 2) + "😀tail";
        Save(store, "summary\0λ", "input\0λ", output, diff, "path\0λ");
        var got = store.ToolCallPrefix("s", "t", RowsFeature.ToolCap, RowsFeature.ToolCap, default)!;
        Assert.Equal(2 * RowsFeature.ToolCap + 3, got.OutputBytes);
        Assert.Equal(RowsFeature.ToolCap, Encoding.UTF8.GetByteCount(got.Output!));
        Assert.Equal(Encoding.UTF8.GetByteCount(diff), got.DiffBytes);
        Assert.Equal(new string('b', RowsFeature.ToolCap - 2), got.Diff);
        Assert.DoesNotContain('�', got.Diff!);
        Assert.Equal(("summary\0λ", "input\0λ", "path\0λ"), (got.Summary, got.Input, got.Path));
        Assert.False(got.OutputTruncated);
        var full = store.ToolCall("s", "t")!;
        Assert.Equal(output, full.Output); Assert.Equal(diff, full.Diff);
        Assert.Equal((got.OutputBytes, got.DiffBytes), (full.OutputBytes, full.DiffBytes));
    }

    [Fact]
    public void Null_empty_missing_and_cancelled_reads_remain_distinct()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var store = RowStore.Open(db);
        Save(store, null, null, null, null, null);
        var absent = store.ToolCallPrefix("s", "t", 1, 1, default)!;
        Assert.Null(absent.Output); Assert.Null(absent.Diff); Assert.Equal(0, absent.OutputBytes); Assert.Equal(0, absent.DiffBytes);
        Save(store, "", "", "", "", "");
        var empty = store.ToolCallPrefix("s", "t", 1, 1, default)!;
        Assert.Equal("", empty.Output); Assert.Equal("", empty.Diff); Assert.Equal(0, empty.OutputBytes); Assert.Equal(0, empty.DiffBytes);
        Assert.Null(store.ToolCallPrefix("missing", "t", 1, 1, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.ToolCallPrefix("s", "t", 1, 1, cancelled.Token));
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("input")]
    [InlineData("path")]
    public void Provably_oversized_uncapped_field_is_refused_without_loading_it(string field)
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var store = RowStore.Open(db); var huge = new string('x', 4 * 1024 * 1024 + 1);
        Save(store, field == "summary" ? huge : "ok", field == "input" ? huge : "ok", "output", "diff", field == "path" ? huge : "ok");
        Assert.Equal("the answer is over 4 MB", Assert.Throws<CallFailed>(() => store.ToolCallPrefix("s", "t", 10, 10, default)).Message);
    }

    [Fact]
    public void Fields_and_counts_use_one_consistent_statement_when_another_connection_commits()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var store = RowStore.Open(db); Save(store, "old", "old", "old€", "old😀", "old");
        using var other = TestDatabase.Open(file.Path); var writer = RowStore.Open(other);
        var measured = new ConcurrentRead(db, () => Save(writer, "new", "new", "new-output", "new-diff", "new"));
        var reader = RowStore.Open(measured); measured.Enabled = true;
        var got = reader.ToolCallPrefix("s", "t", 4, 4, default)!;
        Assert.Equal(1, measured.Reads);
        Assert.Equal(("old", "old", "old", "old"), (got.Summary, got.Input, got.Output, got.Path));
        Assert.Equal((6, 7), (got.OutputBytes, got.DiffBytes));
        Assert.Equal("new-output", writer.ToolCall("s", "t")!.Output);
    }

    [Fact]
    public void Terminal_source_keeps_exact_total_and_states_inside_the_same_requested_window()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var store = RowStore.Open(db); var batch = new RowBatch();
        batch.Details.Add(new("t", AcpSessionId: "provider", TerminalIds: ["terminal"], OutputSource: "terminal"));
        batch.Terminals.Add(new(1, "terminal", Convert.ToBase64String(Encoding.UTF8.GetBytes("abc😀")), AcpSessionId: "provider"));
        store.Commit("s", batch);
        var got = store.ToolCallPrefix("s", "t", 5, 5, default)!;
        Assert.Equal("abc", got.Output); Assert.Equal(7, got.OutputBytes);
        Assert.Equal("terminal", Assert.Single(got.Terminals!).TerminalId);
    }

    private static void Save(RowStore store, string? summary, string? input, string? output, string? diff, string? path)
    {
        var batch = new RowBatch();
        batch.Rows.Add(new(null, "t", "tool", JsonSerializer.SerializeToElement(new { summary }), 1));
        batch.Details.Add(new("t", input, output, diff, path, Replace: true));
        store.Commit("s", batch);
    }

    private sealed class ConcurrentRead(IAppDatabase inner, Action change) : IAppDatabase
    {
        public bool Enabled;
        public int Reads;
        private bool _changed;
        public IAtomicWriter Writer => inner.Writer;
        public IReadSnapshot OpenSnapshot(CancellationToken ct) => new Snapshot(inner.OpenSnapshot(ct), this);
        private Func<IRow, T> Map<T>(Func<IRow, T> map)
        {
            if (Enabled) Reads++;
            return row =>
            {
                if (Enabled && !_changed) { _changed = true; change(); }
                return map(row);
            };
        }
        public List<T> Query<T>(string sql, Func<IRow, T> map, CancellationToken ct, params SqlArg[] args) => inner.Query(sql, Map(map), ct, args);
        public T? QueryFirst<T>(string sql, Func<IRow, T> map, CancellationToken ct, params SqlArg[] args) => inner.QueryFirst(sql, Map(map), ct, args);
        private sealed class Snapshot(IReadSnapshot inner, ConcurrentRead owner) : IReadSnapshot
        {
            public List<T> Query<T>(string sql, Func<IRow, T> map, params SqlArg[] args) => inner.Query(sql, owner.Map(map), args);
            public T? QueryFirst<T>(string sql, Func<IRow, T> map, params SqlArg[] args) => inner.QueryFirst(sql, owner.Map(map), args);
            public void Dispose() => inner.Dispose();
        }
    }
}
