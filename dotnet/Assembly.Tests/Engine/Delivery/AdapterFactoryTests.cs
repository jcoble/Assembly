using Assembly.Tests.Engine;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Features.Chat.Sessions;
using Microsoft.Data.Sqlite;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Delivery;

public sealed class AdapterFactoryTests
{
    [Fact]
    public void Conflicting_effect_sources_are_rejected_before_initialization()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path); var topics = new Topics();
        var called = false; var fake = new FakeEffects { Execute = (_, _) => throw new InvalidOperationException("No provider") };
        Assert.Throws<ArgumentException>(() => EngineRuntime.Start(db, topics, TimeProvider.System, new(() => 1, () => 5), fake,
            effectsFactory: _ => { called = true; return fake; }));
        Assert.False(called); Assert.Equal(0, db.QueryFirst("SELECT COUNT(*) FROM sqlite_master WHERE name='engine_meta'", r => r.Long(0), default));
        Assert.False(topics.HasSubscribers("__engine_committed"));
    }

    [Fact]
    public void Failed_initialization_never_invokes_factory_or_starts_subscriptions()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path); var topics = new Topics(); RowStore.Open(db);
        var sessions = SessionStore.Open(db, TimeProvider.System, "checkouts"); sessions.Create("claude", "repo"); EngineSchema.Open(db, 1000);
        db.Writer.Write("test.block-import", s => s.Execute("CREATE TRIGGER block_import BEFORE INSERT ON engine_session BEGIN SELECT RAISE(ABORT,'import blocked'); END"));
        var launcher = new FakeLauncher(); var called = false;
        Assert.Throws<SqliteException>(() => EngineRuntime.Start(db, topics, TimeProvider.System, new(() => 1, () => 5),
            cutover: new(db, sessions, launcher, "checkouts", TimeProvider.System), effectsFactory: _ => { called = true; throw new InvalidOperationException("No factory"); }));
        Assert.False(called); Assert.Equal(0, launcher.Launches); Assert.False(topics.HasSubscribers("__engine_committed"));
        Assert.Equal(0, db.QueryFirst("SELECT COUNT(*) FROM engine_session", r => r.Long(0), default));
    }

    [Fact]
    public void Failed_factory_runs_once_after_committed_import_and_starts_no_tasks()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path); var topics = new Topics(); RowStore.Open(db);
        var sessions = SessionStore.Open(db, TimeProvider.System, "checkouts"); var session = sessions.Create("claude", "repo");
        var launcher = new FakeLauncher(); var calls = 0;
        var error = Assert.Throws<InvalidOperationException>(() => EngineRuntime.Start(db, topics, TimeProvider.System, new(() => 1, () => 5),
            cutover: new(db, sessions, launcher, "checkouts", TimeProvider.System), effectsFactory: runtime =>
            {
                calls++; Assert.NotNull(runtime.Reader.Entity("session", session.SessionId, default)); Assert.NotNull(runtime.Publisher);
                Assert.Equal(1, db.QueryFirst("SELECT COUNT(*) FROM engine_session", r => r.Long(0), default));
                Assert.Equal(0, launcher.Launches); throw new InvalidOperationException("factory blocked");
            }));
        Assert.Equal("factory blocked", error.Message); Assert.Equal(1, calls); Assert.Equal(0, launcher.Launches);
        Assert.False(topics.HasSubscribers("__engine_committed")); Assert.False(topics.HasSubscribers("__engine_mirrored"));
    }
}
