using System.Collections.Concurrent;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed class SessionRemoteApplyCancellationTests
{
    [Fact]
    public async Task A_cancelled_request_waiting_for_the_real_writer_changes_no_copied_fields_summary_or_publications()
    {
        using var file = new TempDatabase();
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1000));
        var topics = new TopicSet();
        using var real = AppDatabase.Open(file.Path, time, topics);
        var attempted = Signal();
        var observeRemote = false;
        var observed = new AttemptedDatabase(real, () => { if (observeRemote) attempted.TrySetResult(); });
        var summaries = SessionSummaries.Load(RowStore.Open(observed));
        var store = SessionStore.Open(observed, time, "checkouts", summaries);
        const string route = "workbox-test";
        store.Add("copied", "claude", "recorded-repo", route);
        var publications = new ConcurrentQueue<(string Topic, string Json)>();
        var sessions = topics.Subscribe(SessionsFeature.Topic, "copied", _ => { }, (_, json) => publications.Enqueue((SessionsFeature.Topic, json)));
        var rail = topics.Subscribe(SessionsFeature.RailTopic, "copied", _ => { }, (_, json) => publications.Enqueue((SessionsFeature.RailTopic, json)));
        var held = Signal();
        var release = Signal();
        using var request = new CancellationTokenSource();
        Task<int>? holding = null;
        Task<bool>? applying = null;
        try
        {
            // The legacy path still commits on real SQLite and publishes after setup.
            store.ApplyRemote(store.Item(store.Get("copied")!) with
            {
                Title = "before\0λ",
                TitleSource = "prompt",
                UpdatedAt = 2000,
                CheckoutRoot = "/source-derived/before",
                Branch = "before"
            });
            Assert.Contains(publications, publication => publication.Topic == SessionsFeature.Topic);
            Assert.Contains(publications, publication => publication.Topic == SessionsFeature.RailTopic);
            publications.Clear();
            var before = store.Get("copied")!;
            var beforeItem = store.Item(before);
            var beforeSummary = summaries.Get("copied");
            var beforeAudit = real.QueryFirst("SELECT COUNT(*) FROM audit WHERE what='sessions.remote'", row => row.Long(0), default);
            var callbackCalls = 0;
            observeRemote = true;
            holding = Task.Run(() => real.Writer.Write("test.hold-writer", _ =>
            {
                held.TrySetResult();
                release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                return 0;
            }));
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var incoming = beforeItem with
            {
                Title = "cancelled update",
                TitleSource = "user",
                UpdatedAt = 3000,
                CheckoutRoot = "/source-derived/after",
                Branch = "after",
                State = "running",
                ActiveRunId = "source-derived-run",
                PendingAsk = true
            };
            applying = Task.Run(() => store.ApplyRemote(incoming, route, scope =>
            {
                Interlocked.Increment(ref callbackCalls);
                Assert.Equal(before.Title, scope.QueryFirst("SELECT CAST(title AS BLOB) FROM session_info WHERE session_id='copied'",
                    row => System.Text.Encoding.UTF8.GetString(row.Blob(0))));
                request.Token.ThrowIfCancellationRequested();
            }));
            await attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(applying.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref callbackCalls));
            request.Cancel();
            release.TrySetResult();
            await holding.WaitAsync(TimeSpan.FromSeconds(10));
            var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => applying.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(request.Token, cancelled.CancellationToken);
            Assert.Equal(1, callbackCalls);
            Assert.Equal(before, store.Get("copied"));
            Assert.Equal(beforeItem, store.Item(store.Get("copied")!));
            Assert.Equal(beforeSummary, summaries.Get("copied"));
            Assert.Equal(beforeAudit, real.QueryFirst("SELECT COUNT(*) FROM audit WHERE what='sessions.remote'", row => row.Long(0), default));
            Assert.Empty(publications);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                if (holding is not null) await holding.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                try
                {
                    if (applying is not null)
                    {
                        try { await applying.WaitAsync(TimeSpan.FromSeconds(10)); }
                        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                    }
                }
                finally { topics.Unsubscribe(sessions); topics.Unsubscribe(rail); }
            }
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Observes entry to the actual writer; it never substitutes a scope, transaction or successful result.
    private sealed class AttemptedDatabase(IAppDatabase inner, Action attempting) : IAppDatabase
    {
        public IAtomicWriter Writer { get; } = new AttemptedWriter(inner.Writer, attempting);
        public IReadSnapshot OpenSnapshot(CancellationToken ct) => inner.OpenSnapshot(ct);
        public List<T> Query<T>(string sql, Func<IRow, T> map, CancellationToken ct, params SqlArg[] args) => inner.Query(sql, map, ct, args);
        public T? QueryFirst<T>(string sql, Func<IRow, T> map, CancellationToken ct, params SqlArg[] args) => inner.QueryFirst(sql, map, ct, args);
        private sealed class AttemptedWriter(IAtomicWriter inner, Action attempting) : IAtomicWriter
        {
            public T Write<T>(string what, Func<IWriteScope, T> write)
            {
                if (what == "sessions.remote") attempting();
                return inner.Write(what, write);
            }
        }
    }
}
