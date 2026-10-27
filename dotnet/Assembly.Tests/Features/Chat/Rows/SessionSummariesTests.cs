using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>The summaries a start fills from one grouped query agree with each session's own queries.</summary>
public sealed class SessionSummariesTests : IDisposable
{
    private readonly TempDatabase _file = new();

    [Fact]
    public void A_start_fills_what_each_sessions_own_queries_find()
    {
        using var db = TestDatabase.Open(_file.Path);
        var store = RowStore.Open(db);
        // A finished turn then a running one, with an open ask and work left; a failed one with a task done; one with
        // nothing but chat; one cancelled with a subagent waiting; and one with no rows.
        Commit(store, "a", ("r1", null, RowKind.Turn, "started"), ("r1", null, RowKind.Turn, "finished"), ("r2", null, RowKind.Turn, "started"),
            ("r2", "q1", RowKind.Ask, "open"), ("r2", "q2", RowKind.Ask, "open"), ("r2", "q2", RowKind.Ask, "answered"),
            ("r2", "t1", RowKind.Task, "running"), ("r2", "t2", RowKind.Task, "requires_action"));
        Commit(store, "b", ("r1", null, RowKind.Turn, "started"), ("r1", "t1", RowKind.Task, "running"), ("r1", null, RowKind.Turn, "failed"),
            ("r1", "t1", RowKind.Task, "completed"));
        Commit(store, "c", ("r1", "m1", RowKind.Message, null), ("r1", null, RowKind.User, null));
        Commit(store, "d", ("r1", null, RowKind.Turn, "started"), ("r1", "s1", RowKind.Subagent, "running"), ("r1", "s1", RowKind.Subagent, "requires_action"),
            ("r1", null, RowKind.Turn, "cancelled"));

        var summaries = SessionSummaries.Load(store);

        foreach (var session in new[] { "a", "b", "c", "d", "none" })
            Assert.Equal(Expected(db, session), summaries.Get(session));
        Assert.Equal(new SessionSummary("r2", "started", 2, true, 2), summaries.Get("a"));
    }

    // Set ASSEMBLY_SUMMARY_DB to a database to check it too; the test works on a copy.
    [BigDatabaseFact]
    public void A_start_fills_what_each_sessions_own_queries_find_on_a_big_database()
    {
        File.Copy(Environment.GetEnvironmentVariable(BigDatabaseFactAttribute.Variable)!, _file.Path, overwrite: true);
        using var db = TestDatabase.Open(_file.Path);
        var store = RowStore.Open(db);

        var summaries = SessionSummaries.Load(store);

        var sessions = db.Query("SELECT DISTINCT session_id FROM rows;", row => row.Text(0), CancellationToken.None);
        Assert.NotEmpty(sessions);
        foreach (var session in sessions)
            Assert.Equal(Expected(db, session), summaries.Get(session));
    }

    public void Dispose() => _file.Dispose();

    // The session's summary from its own queries: its newest turn row, its newest ended turn, its open asks, and its
    // tasks and subagents at work.
    private static SessionSummary Expected(AppDatabase db, string session)
    {
        var args = new SqlArg("$s", session);
        var turn = db.Query("SELECT run_id, json_extract(payload, '$.state') FROM rows WHERE session_id = $s AND kind = 'turn' ORDER BY seq DESC LIMIT 1;",
            row => (Run: row.IsNull(0) ? null : row.Text(0), State: row.Text(1)), CancellationToken.None, args).FirstOrDefault();
        var end = db.QueryFirst("""
            SELECT COALESCE(MAX(seq), 0) FROM rows WHERE session_id = $s AND kind = 'turn'
              AND json_extract(payload, '$.state') IN ('finished', 'failed', 'cancelled');
            """, row => row.Long(0), CancellationToken.None, args);
        var ask = db.QueryFirst("""
            SELECT EXISTS (SELECT 1 FROM rows r WHERE r.session_id = $s AND r.kind = 'ask' AND json_extract(r.payload, '$.state') = 'open'
              AND NOT EXISTS (SELECT 1 FROM rows e WHERE e.session_id = r.session_id AND e.item_id = r.item_id AND e.kind = 'ask' AND e.seq > r.seq));
            """, row => row.Long(0) == 1, CancellationToken.None, args);
        var background = db.QueryFirst("""
            SELECT COUNT(*) FROM rows r WHERE r.session_id = $s AND r.kind IN ('task', 'subagent')
              AND json_extract(r.payload, '$.state') IN ('running', 'requires_action')
              AND NOT EXISTS (SELECT 1 FROM rows e WHERE e.session_id = r.session_id AND e.item_id = r.item_id AND e.kind = r.kind AND e.seq > r.seq);
            """, row => (int)row.Long(0), CancellationToken.None, args);
        return new SessionSummary(turn.State == "started" ? turn.Run : null, turn.State, end, ask, background);
    }

    private static void Commit(RowStore store, string session, params (string Run, string? Item, string Kind, string? State)[] rows)
    {
        var batch = new RowBatch();
        foreach (var (run, item, kind, state) in rows)
            batch.Rows.Add(new NewRow(run, item, kind, JsonSerializer.SerializeToElement(new { state, text = "x" }), 1));
        store.Commit(session, batch);
    }
}

/// <summary>A test of a database named by <see cref="Variable"/>: skipped when it's unset.</summary>
public sealed class BigDatabaseFactAttribute : FactAttribute
{
    public const string Variable = "ASSEMBLY_SUMMARY_DB";

    public BigDatabaseFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"set {Variable} to a database to check";
    }
}
