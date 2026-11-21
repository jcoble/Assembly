using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Rows.Copies;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Chat;
using Assembly.Tests.Draft.Base.Chat;

namespace Assembly.Tests.Infrastructure.Remote.Copies;

/// <summary>
/// The download of a remote session into its saved copy here (TSK-1692), between two real hosts: the remote serves its
/// rows reads, and this machine's <c>RemoteMachine</c> downloads them newest first.
/// </summary>
public sealed class RemoteDownloadTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly TwoHosts _hosts = new();
    private RowStore _remoteRows = null!;
    private LiveHub? _remoteHub;
    // Each rows/before call the remote answered: its args and the seqs it sent.
    private readonly ConcurrentQueue<(RowsFeature.BeforeArgs Args, long[] Seqs)> _answered = new();
    private Func<RowsFeature.BeforeArgs, CancellationToken, Task>? _beforeReading;
    // Each session whose download reached the bottom: the remote answered it with no rows. Recorded from the start.
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _bottomed = new();

    public async Task InitializeAsync()
    {
        _hosts.Remote.AdditionalFeatures = calls =>
        {
            _remoteHub?.Dispose();
            _remoteRows = RowStore.Open(_hosts.Remote.Fixture.Db);
            _remoteHub = new LiveHub(_remoteRows, topics: _hosts.Remote.Topics);
            new RowsFeature(_remoteHub, _remoteRows, _hosts.Remote.Sessions, new MachineSet(new FakeMachine(null, _hosts.Remote.Sessions)),
                new DiskPathProbe(), Path.GetTempPath()).Register(new HeldReads(calls, this));
        };
        await _hosts.Start();
        // The remote's own record of its session, which its live rows reads look up.
        _hosts.Remote.Sessions.Add("s", "codex", "/test", null!);
        await _hosts.StartMirror();
    }

    // Pins: spec test 2.
    [Fact]
    public async Task Spec02_after_the_download_the_copy_equals_the_remote()
    {
        Seed("s", 350);
        // A message still streaming at the top keeps its deltas.
        Save("s", 351, "streaming", RowKind.Delta);
        Save("s", 352, "streaming", RowKind.Delta);

        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        await DownloadedAsync("s");

        Assert.Equal(RemoteRows("s"), MacRows("s"));
        Assert.Equal(new RemoteCopy("s", _hosts.Remote.Runtime.Machine, true, 352), _hosts.MacHub.Copies.RemoteCopyOf("s"));
        Assert.Equal(352, _hosts.MacHub.LastSeq("s"));
        // The newest 100 items: the streaming message and 252..350.
        Assert.Equal(Enumerable.Range(252, 101).Select(seq => (long)seq), _answered.First().Seqs.Order());
    }

    // Pins: spec test 3.
    [Fact]
    public async Task Spec03_live_rows_during_the_download_are_saved_once_in_order()
    {
        Seed("s", 350);
        var backfill = HoldBackfill();
        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        await backfill.Reached.WaitAsync(Wait);

        using var viewer = _hosts.MacHub.Subscribe("s", _hosts.MacHub.LastSeq("s"));
        Machine.WatchRun("s", "a-run-still-to-come");
        for (var seq = 351; seq <= 355; seq++)
            Assert.True(_remoteHub!.PublishCopy(Row("s", seq, $"live-{seq}") with { PrevSeq = seq - 1 }));
        // Bulk calls take turns, so the follower's catch-up read runs once the held chunk is answered.
        backfill.Release();
        for (var seq = 351; seq <= 355; seq++)
            Assert.Equal(seq, (await viewer.Queue.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Seq);
        await DownloadedAsync("s");

        Assert.Equal(RemoteRows("s"), MacRows("s"));
        Assert.Equal(355, _hosts.MacHub.LastSeq("s"));
    }

    // Pins: spec test 5: an item is never split across chunks.
    [Fact]
    public async Task Spec05_an_item_split_across_chunks_is_saved_whole()
    {
        Seed("s", 350, seq => seq is 5 or 200 or 349 ? "split" : null);

        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        await DownloadedAsync("s");

        Assert.Equal(RemoteRows("s"), MacRows("s"));
        Assert.All(_answered, answer =>
        {
            var split = answer.Seqs.Count(seq => seq is 5 or 200 or 349);
            Assert.True(split is 0 or 3, $"a chunk carried {split} of the split item's 3 rows");
        });
    }

    // Pins: spec test 6.
    [Fact]
    public async Task Spec06_a_download_cut_midway_finishes_on_the_next_connection()
    {
        Seed("s", 350);
        var backfill = HoldBackfill();
        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        await backfill.Reached.WaitAsync(Wait);

        await _hosts.Remote.Stop();
        Assert.False(_hosts.MacHub.Copies.RemoteCopyOf("s")!.Complete);
        _beforeReading = null;
        await _hosts.Remote.Restart();
        await Machine.ProbeAsync(default).WaitAsync(Wait);
        await DownloadedAsync("s");

        Assert.Equal(RemoteRows("s"), MacRows("s"));
        Assert.True(_hosts.MacHub.Copies.RemoteCopyOf("s")!.Complete);
    }

    // Pins: spec test 19: one child in the first chunk, one in a backfill chunk, under a parent known only from the mirror.
    [Fact]
    public async Task Spec19_a_subagent_child_exists_after_its_parent_downloads()
    {
        Assert.Null(_hosts.Mac.Sessions.Get("s"));
        Seed("s", 350);
        Card("s", 351, "child-new", "native-new");
        Card("s", 20, "child-old", "native-old");

        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        Assert.NotNull(_hosts.Mac.Sessions.Get("child-new"));
        await DownloadedAsync("s");

        foreach (var (child, native) in new[] { ("child-new", "native-new"), ("child-old", "native-old") })
        {
            var made = _hosts.Mac.Sessions.Get(child);
            Assert.NotNull(made);
            Assert.Equal(("s", native, "workbox-test"), (made.ParentSessionId, made.AcpSessionId, made.Machine));
        }
    }

    // Pins: review focus 2.
    [Fact]
    public async Task First_open_of_a_followed_session_adopts_its_forward_copy()
    {
        Seed("s", 350);
        // The follower copied the session forward from its start before it was ever opened.
        for (var seq = 1; seq <= 350; seq++)
            Assert.True(_hosts.MacHub.PublishCopy(Row("s", seq, null) with { PrevSeq = seq - 1 }));

        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        await DownloadedAsync("s");

        Assert.DoesNotContain(_answered, answer => answer.Args.Session == "s" && answer.Args.Before is null);
        Assert.Equal(RemoteRows("s"), MacRows("s"));
        Assert.Equal(350, _hosts.MacHub.LastSeq("s"));
    }

    [Fact]
    public async Task Opening_a_downloading_session_again_joins_the_running_download()
    {
        Seed("s", 350);
        var first = Hold(args => args.Session == "s" && args.Before is null);

        var opened = Machine.EnsureCopyAsync("s", default);
        await first.Reached.WaitAsync(Wait);
        var again = Machine.EnsureCopyAsync("s", default);
        first.Release();
        await Task.WhenAll(opened, again).WaitAsync(Wait);
        await DownloadedAsync("s");

        Assert.Single(_answered, answer => answer.Args.Session == "s" && answer.Args.Before is null);
        Assert.Equal(RemoteRows("s"), MacRows("s"));
    }

    [Fact]
    public async Task Connecting_never_waits_on_a_download_call()
    {
        Seed("s", 350);
        var first = Hold(args => args.Session == "s" && args.Before is null);
        var opened = Machine.EnsureCopyAsync("s", default);
        await first.Reached.WaitAsync(Wait);

        // The machine socket answers, and opens again, while the chunk is held.
        await Machine.ProbeAsync(default).WaitAsync(Wait);
        await Machine.CallAsync("engine", "machine", JsonSerializer.SerializeToElement(new { }), false, default).WaitAsync(Wait);
        Assert.False(opened.IsCompleted);

        first.Release();
        await opened.WaitAsync(Wait);
    }

    private Assembly.Infrastructure.Remote.RemoteMachine Machine => _hosts.ConnectedMachine;

    // One download runs at a time, so a second session's first chunk is saved only once this one's download has ended.
    private async Task DownloadedAsync(string session)
    {
        await Bottomed(session).Task.WaitAsync(Wait);
        var other = $"after-{session}-{Guid.NewGuid():N}";
        Seed(other, 1);
        await Machine.EnsureCopyAsync(other, default).WaitAsync(Wait);
        Assert.True(_hosts.MacHub.Copies.RemoteCopyOf(session)?.Complete);
    }

    private TaskCompletionSource Bottomed(string session) =>
        _bottomed.GetOrAdd(session, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    private sealed record Held(Task Reached, Action Release);

    private Held HoldBackfill() => Hold(args => args.Session == "s" && args.Before is not null);

    private Held Hold(Func<RowsFeature.BeforeArgs, bool> which)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeReading = (args, ct) =>
        {
            if (!which(args)) return Task.CompletedTask;
            reached.TrySetResult();
            return release.Task.WaitAsync(ct);
        };
        return new(reached.Task, () => release.TrySetResult());
    }

    private void Seed(string session, int count, Func<int, string?>? item = null)
    {
        for (var seq = 1; seq <= count; seq++) Save(session, seq, item?.Invoke(seq));
    }

    private void Save(string session, long seq, string? item, string kind = RowKind.Tool) =>
        Assert.True(_remoteRows.Copy(Row(session, seq, item, kind) with { PrevSeq = seq - 1 }));

    // A subagent card: appended at the top, or written over an older row already saved.
    private void Card(string session, long seq, string child, string native)
    {
        var payload = JsonSerializer.SerializeToElement(new { sessionId = child, title = child, state = "running" });
        if (RemoteRows(session).Count >= seq)
        {
            _hosts.Remote.Fixture.Db.Writer.Write("test.card", scope => scope.Execute(
                "UPDATE rows SET item_id=$item, kind=$kind, payload=$payload WHERE session_id=$session AND seq=$seq",
                new SqlArg("$item", $"{CopyRecords.SubagentItem}{native}"), new SqlArg("$kind", RowKind.Subagent),
                new SqlArg("$payload", payload.GetRawText()), new SqlArg("$session", session), new SqlArg("$seq", seq)));
            return;
        }
        Assert.True(_remoteRows.Copy(new StoredRow(session, seq, "run", $"{CopyRecords.SubagentItem}{native}", RowKind.Subagent,
            payload, seq, PrevSeq: seq - 1)));
    }

    private static StoredRow Row(string session, long seq, string? item, string kind = RowKind.Tool) =>
        new(session, seq, "run", item ?? $"item-{seq}", kind, JsonSerializer.SerializeToElement(new { text = $"row {seq}" }), seq);

    private IReadOnlyList<string> RemoteRows(string session) => Rows(_hosts.Remote.Fixture.Db, session);

    private IReadOnlyList<string> MacRows(string session) => Rows(_hosts.Mac.Fixture.Db, session);

    private static IReadOnlyList<string> Rows(IAppDatabase db, string session) => db.Query(
        "SELECT seq, item_id, kind, payload FROM rows WHERE session_id=$s ORDER BY seq",
        row => $"{row.Long(0)} {row.Text(1)} {row.Text(2)} {row.Text(3)}", default, new SqlArg("$s", session));

    public async Task DisposeAsync()
    {
        _beforeReading = null;
        await _hosts.DisposeAsync();
        _remoteHub?.Dispose();
    }

    // The remote's rows reads, observed: each before call can be held, and each answer is recorded.
    private sealed class HeldReads(ICallRegistry inner, RemoteDownloadTests test) : ICallRegistry
    {
        public void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
            Func<TArgs, CallContext, Task<TResult>> handler, bool bulk = false) =>
            inner.Read(feature, method, args, result, async (value, call) =>
            {
                if (value is RowsFeature.BeforeArgs before && test._beforeReading is { } reading)
                    await reading(before, call.Cancellation);
                var answer = await handler(value, call);
                if (value is RowsFeature.BeforeArgs asked && answer is BeforeChunk chunk)
                {
                    test._answered.Enqueue((asked, [.. chunk.Rows.Select(row => row.GetProperty("seq").GetInt64())]));
                    if (chunk.Rows.Count == 0) test.Bottomed(asked.Session).TrySetResult();
                }
                return answer;
            }, bulk);

        public void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
            Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler, Func<TArgs, string?>? order = null,
            Action<TArgs, CallContext>? failed = null, bool bulk = false) => inner.Write(feature, method, args, result, handler, order, failed, bulk);

        public void Save<T>(string feature, JsonTypeInfo<T> type, Action<string, T, IWriteScope> save) => inner.Save(feature, type, save);

        public void Closed(Action<string> connection) => inner.Closed(connection);

        public void Topic<T>(string topic, JsonTypeInfo<T> type, Func<T, string> key,
            Func<string?, CallContext, IReadOnlyList<T>> state, bool terminal = false) => inner.Topic(topic, type, key, state, terminal);

        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state,
            Action<string, CallContext>? opened = null) => inner.Rows(topic, state, opened);
    }
}
