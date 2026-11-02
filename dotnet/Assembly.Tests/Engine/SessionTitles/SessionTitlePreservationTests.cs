using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Features.Chat.Sessions;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.SessionTitles;

public sealed class SessionTitlePreservationTests
{
    public static TheoryData<string> ExactTitles => new()
    {
        "\0AFTER_NUL",
        "before\0AFTER_NUL",
        "trailing\0",
        new string('x', 1024) + "\0AFTER_NUL😀",
        new string('x', 40000),
        string.Concat(Enumerable.Repeat("😀\"\\", 5000))
    };

    [Theory]
    [MemberData(nameof(ExactTitles))]
    public async Task Actual_title_observer_preserves_complete_bytes_without_a_new_title_cap(string title)
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics);
        var sessions = SessionStore.Open(f.Db, f.Time, "/checkouts");
        var session = sessions.Create("claude", "repo"); var launcher = new FakeLauncher();
        await using (var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits,
            cutover: new(f.Db, sessions, launcher, "/checkouts", f.Time)))
        {
            sessions.SetTitle(session.SessionId, title, "user");
            var expected = new UTF8Encoding(false, true).GetBytes(title);
            Assert.Equal(expected, f.Db.QueryFirst("SELECT CAST(title AS BLOB) FROM session_info WHERE session_id=$id",
                r => r.Blob(0), default, new SqlArg("$id", session.SessionId)));
            Assert.Equal(expected, f.Db.QueryFirst("SELECT CAST(title AS BLOB) FROM engine_session WHERE id=$id",
                r => r.Blob(0), default, new SqlArg("$id", session.SessionId)));
            Assert.Equal(title, sessions.Get(session.SessionId)!.Title);
            Assert.Equal(0, launcher.Launches);
        }
        f.Reopen();
        Assert.Equal(title, SessionStore.Open(f.Db, f.Time, "/checkouts").Get(session.SessionId)!.Title);
    }
    [Fact]
    public async Task Actual_title_priority_source_upgrade_native_and_foreign_exclusions_stay_unchanged()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics);
        var sessions = SessionStore.Open(f.Db, f.Time, "/checkouts");
        var owner = sessions.Create("claude", "repo");
        var native = sessions.Child(owner, "native-child", "Native\0full");
        var foreign = sessions.Add("foreign", "claude", "repo", "workbox-test");
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits,
            cutover: new(f.Db, sessions, new FakeLauncher(), "/checkouts", f.Time));
        var title = new string('x', 40000) + "\0suffix";
        sessions.SetTitle(owner.SessionId, title, "helper");
        var version = f.Number("SELECT version FROM engine_session WHERE id=$id", new SqlArg("$id", owner.SessionId));
        var events = f.Count("engine_event"); var receipts = f.Count("engine_command_receipt");
        sessions.SetTitle(owner.SessionId, title, "user"); // Accepted source-only upgrade publishes legacy metadata, no title revision.
        Assert.Equal("user", sessions.Get(owner.SessionId)!.TitleSource);
        Assert.Equal(version, f.Number("SELECT version FROM engine_session WHERE id=$id", new SqlArg("$id", owner.SessionId)));
        Assert.Equal(events, f.Count("engine_event")); Assert.Equal(receipts, f.Count("engine_command_receipt"));
        sessions.SetTitle(owner.SessionId, "losing proposal", "helper"); Assert.Equal(title, sessions.Get(owner.SessionId)!.Title);
        sessions.SetTitle(owner.SessionId, "equal-rank\0replacement", "user");
        Assert.Equal(version + 1, f.Number("SELECT version FROM engine_session WHERE id=$id", new SqlArg("$id", owner.SessionId)));
        sessions.SetTitle(native.SessionId, "Native\0changed", "user");
        sessions.SetTitle(foreign.SessionId, "Foreign\0changed", "user");
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_session WHERE id IN ($native,$foreign)", new SqlArg("$native", native.SessionId), new SqlArg("$foreign", foreign.SessionId)));
        Assert.Equal("Native\0changed", sessions.Get(native.SessionId)!.Title);
        Assert.Equal("Foreign\0changed", sessions.Get(foreign.SessionId)!.Title);
    }

    [Fact]
    public async Task Actual_title_writer_rolls_back_chat_engine_receipt_sequence_audit_and_push_together()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics);
        var sessions = SessionStore.Open(f.Db, f.Time, "/checkouts"); var session = sessions.Create("claude", "repo");
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits,
            cutover: new(f.Db, sessions, new FakeLauncher(), "/checkouts", f.Time));
        sessions.SetTitle(session.SessionId, "original\0title", "helper");
        f.Db.Writer.Write("test.receipt-failure", scope => scope.Execute("""
            CREATE TRIGGER fail_title_receipt BEFORE INSERT ON engine_command_receipt
            BEGIN SELECT RAISE(ABORT,'receipt rejected'); END;
            """));
        var events = f.Count("engine_event"); var receipts = f.Count("engine_command_receipt");
        var seq = f.Number("SELECT next_seq FROM engine_meta");
        var audits = f.Number("SELECT COUNT(*) FROM audit WHERE what='sessions'"); var pushes = 0;
        using var subscription = topics.Subscribe(SessionsFeature.Topic, session.SessionId, _ => { }, (_, _) => pushes++);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => sessions.SetTitle(session.SessionId, new string('x', 40000) + "\0suffix", "user"));
        Assert.Equal("original\0title", sessions.Get(session.SessionId)!.Title); Assert.Equal("helper", sessions.Get(session.SessionId)!.TitleSource);
        Assert.Equal(Encoding.UTF8.GetBytes("original\0title"), f.Db.QueryFirst("SELECT CAST(title AS BLOB) FROM engine_session WHERE id=$id", r => r.Blob(0), default, new SqlArg("$id", session.SessionId)));
        Assert.Equal(events, f.Count("engine_event")); Assert.Equal(receipts, f.Count("engine_command_receipt"));
        Assert.Equal(seq, f.Number("SELECT next_seq FROM engine_meta")); Assert.Equal(audits, f.Number("SELECT COUNT(*) FROM audit WHERE what='sessions'"));
        Assert.Equal(0, pushes);
    }

}
