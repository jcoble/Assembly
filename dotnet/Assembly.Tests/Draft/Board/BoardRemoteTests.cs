using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Clock;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Workflows;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Application.Features.Workflows.Contracts;

namespace Assembly.Tests.Draft.Board;

public sealed class BoardRemoteTests
{
    [Fact]
    public void ReplayOver100Events()
    {
        using var f = new BoardQueryTests.Fixture();
        for (var i = 0; i < 105; i++) f.Update($"task-{i}");
        var first = f.Queries.Changes(new(0), CancellationToken.None);
        Assert.Equal(100, first.Events.Length);
        Assert.Equal(100, first.NextSeq);
        Assert.Equal(105, first.LatestSeq);
        var last = f.Queries.Changes(new(first.NextSeq), CancellationToken.None);
        Assert.Equal(5, last.Events.Length);
        Assert.Equal(last.LatestSeq, last.NextSeq);
        var empty = f.Queries.Changes(new(last.NextSeq), CancellationToken.None);
        Assert.Empty(empty.Events);
        Assert.Equal(last.NextSeq, empty.NextSeq);
    }

    [Fact]
    public void MultiRowEventCommitsTogether()
    {
        using var source = new BoardQueryTests.Fixture();
        using var desktop = new BoardQueryTests.Fixture();
        source.Update(question: source.Ask(), paths: ["path"]);
        var item = Assert.Single(source.Queries.Changes(new(0), CancellationToken.None).Events);
        desktop.Transitions.Apply(item, "Workbox Test");
        var detail = desktop.Detail(machine: "Workbox Test");
        Assert.Equal("Workbox Test", detail.Task.MachineId);
        Assert.Equal("Workbox Test", Assert.Single(detail.Questions).MachineId);
        Assert.Equal("Workbox Test", Assert.Single(detail.Feed).Task.MachineId);
        Assert.Equal(1, desktop.Queries.Applied("Workbox Test", CancellationToken.None));
        Assert.Empty(desktop.Queries.Changes(new(0), CancellationToken.None).Events);
    }

    [Fact]
    public void CrashBeforeMirrorCommitReplaysOnce()
    {
        using var source = new BoardQueryTests.Fixture();
        using var desktop = new BoardQueryTests.Fixture();
        source.Update(question: source.Ask());
        var item = Assert.Single(source.Queries.Changes(new(0), CancellationToken.None).Events);
        desktop.Db.Writer.Write("test.abort-trigger", s => s.Execute(
            "CREATE TRIGGER abort_event BEFORE INSERT ON board_events BEGIN SELECT RAISE(ABORT,'test crash'); END;"));
        Assert.ThrowsAny<Exception>(() => desktop.Transitions.Apply(item, "box"));
        Assert.Equal(0, desktop.Queries.Applied("box", CancellationToken.None));
        Assert.Empty(desktop.Page().Items);
        desktop.Db.Writer.Write("test.drop-trigger", s => s.Execute("DROP TRIGGER abort_event;"));
        desktop.Transitions.Apply(item, "box");
        Assert.Single(desktop.Page().Items);
    }

    [Fact]
    public void DuplicatePageDoesNotRepublish()
    {
        using var source = new BoardQueryTests.Fixture();
        using var desktop = new BoardQueryTests.Fixture();
        source.Update();
        var item = Assert.Single(source.Queries.Changes(new(0), CancellationToken.None).Events);
        var changes = 0;
        var sub = desktop.Topics.Subscribe("board", null, _ => { }, (_, _) => changes++);
        desktop.Transitions.Apply(item, "box");
        desktop.Transitions.Apply(item, "box");
        Assert.Equal(1, changes);
        Assert.Single(desktop.Detail(machine: "box").Feed);
        desktop.Topics.Unsubscribe(sub);
    }

    [Fact]
    public async Task SourceChangeDuringCatchupIsRetained()
    {
        await using var source = new BoardSocketTests.Host();
        await source.StartAsync();
        using var desktop = new BoardQueryTests.Fixture();
        for (var i = 0; i < 105; i++) source.F.Update($"task-{i}");
        var caught = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = desktop.Topics.Subscribe("board", null, _ => { }, (_, json) =>
        {
            var change = JsonSerializer.Deserialize(json, WorkflowJson.Default.BoardChange)!;
            if (change.Seq == 1) source.F.Update("during-catchup");
            if (change.TaskKey == "during-catchup") caught.TrySetResult();
        });
        await using var mirror = new RemoteWorkflowMirror(new("Workbox Test", source.Address, "board-test"),
            desktop.Queries, desktop.Transitions, () => { });
        mirror.Reconnect();
        await caught.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(106, desktop.Queries.Applied("Workbox Test", CancellationToken.None));
        Assert.Equal(106, desktop.Page().Counts.Total);
        desktop.Topics.Unsubscribe(sub);
    }

    [Fact]
    public async Task SourceRepliesUseConfiguredIdentity()
    {
        await using var source = new BoardSocketTests.Host();
        await source.StartAsync();
        var creator = source.Sessions.Create("codex", "project");
        source.Tools.Update(creator.SessionId, source.F.Report(question: source.F.Ask()));
        await using var desktop = new BoardSocketTests.Host();
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var mirror = new RemoteWorkflowMirror(new("Workbox Test", source.Address, "board-test"),
            desktop.F.Queries, desktop.F.Transitions, () => lost.TrySetResult());
        await desktop.StartAsync(mirror);
        var detail = await mirror.TaskAsync(new("Workbox Test", "task", null, null), CancellationToken.None);
        Assert.Equal("Workbox Test", detail.Task.MachineId);
        Assert.Equal("Workbox Test", Assert.Single(detail.Questions).MachineId);
        Assert.Equal("Workbox Test", Assert.Single(detail.Feed).MachineId);
        Assert.Equal("Workbox Test", detail.Feed[0].Task.MachineId);
        Assert.Equal("Workbox Test", detail.Feed[0].Questions[0].MachineId);
        var args = source.F.Answer() with { MachineId = "Workbox Test" };
        var accepted = await mirror.AnswerAsync(args, CancellationToken.None);
        Assert.Equal("Workbox Test", accepted.MachineId);
        Assert.Equal(args.AnswerKey, accepted.AnswerKey);
        Assert.Equal(args.Generation, accepted.Generation);
        Assert.Single(source.Rows.RowsAfter(creator.SessionId, 0), row => row.Kind == "user");
        using var socket = await desktop.ConnectAsync();
        await BoardSocketTests.Send(socket, """{"t":"call","id":1,"feature":"board","method":"task","args":{"machineId":"unknown","taskKey":"task","beforeSeq":null,"questionCursor":null}}""");
        Assert.Equal("unreachable", (await BoardSocketTests.Read(socket)).GetProperty("code").GetString());
        await source.StopAsync();
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var before = desktop.F.Queries.Applied("Workbox Test", CancellationToken.None);
        await Assert.ThrowsAsync<MachineUnreachable>(() => mirror.AnswerAsync(args, CancellationToken.None));
        Assert.Equal(before, desktop.F.Queries.Applied("Workbox Test", CancellationToken.None));
        Assert.Empty(desktop.F.Queries.Changes(new(0), CancellationToken.None).Events);
    }

    [Fact]
    public async Task CancelledSourceCallKeepsTheBoardConnection()
    {
        await using var source = new BoardSocketTests.Host();
        await source.StartAsync();
        var creator = source.Sessions.Create("codex", "project");
        // A running turn holds the answer on the source until the caller cancels it.
        source.Registry.For(creator.SessionId).Send("held");
        source.Tools.Update(creator.SessionId, source.F.Report(question: source.F.Ask()));
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = source.F.Topics.Subscribe("board", null, _ => { }, (_, _) => recorded.TrySetResult());
        using var desktop = new BoardQueryTests.Fixture();
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var mirror = new RemoteWorkflowMirror(new("Workbox Test", source.Address, "board-test"),
            desktop.Queries, desktop.Transitions, () => lost.TrySetResult());
        var args = source.F.Answer() with { MachineId = "Workbox Test" };
        using var cancel = new CancellationTokenSource();
        var waiting = mirror.AnswerAsync(args, cancel.Token);
        await recorded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        // The same answer again queues behind the cancelled one on the source, so its reply follows the cancel's reply.
        Assert.Equal(args.AnswerKey, (await mirror.AnswerAsync(args, CancellationToken.None)).AnswerKey);
        Assert.False(lost.Task.IsCompleted);
        source.F.Topics.Unsubscribe(sub);
    }

    [Fact]
    public void RestartOfflineRetainsCardsAndRoutesToSource()
    {
        using var source = new BoardQueryTests.Fixture();
        using var file = new TempDatabase();
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        source.Update(question: source.Ask());
        var item = Assert.Single(source.Queries.Changes(new(0), CancellationToken.None).Events);
        using (var db = AppDatabase.Open(file.Path, source.Time, topics))
        {
            global::Assembly.Application.Features.Workflows.WorkflowSchema.Open(db);
            new global::Assembly.Application.Features.Workflows.WorkflowTransitions(db, source.Time).Apply(item, "Workbox Test");
        }
        using var reopened = AppDatabase.Open(file.Path, source.Time, topics);
        var queries = new global::Assembly.Application.Features.Workflows.WorkflowQueries(reopened);
        Assert.Equal("Workbox Test", Assert.Single(queries.Page(new(null, null, null), CancellationToken.None).Items).Task.MachineId);
        Assert.Equal(1, queries.Applied("Workbox Test", CancellationToken.None));
        // Routing and offline failures are exercised over the actual socket in SourceRepliesUseConfiguredIdentity.
    }

    [Fact]
    public async Task ReconnectWithoutWindowSubscriber()
    {
        await using var source = new BoardSocketTests.Host();
        await source.StartAsync();
        using var desktop = new BoardQueryTests.Fixture();
        var status = new MachineStatus(desktop.Topics, ["Workbox Test"]);
        var far = new FakeMachine("Workbox Test", source.Sessions);
        await using var work = new BackgroundWork(desktop.Time, TextWriter.Null);
        var clock = new BackendClock(desktop.Topics, desktop.Time, work);
        await using var machines = new MachinesFeature(new MachineSet(new FakeMachine(null, source.Sessions), far),
            status, clock.Start, desktop.Time);
        clock.Ticked += machines.Tick;
        await using var mirror = new RemoteWorkflowMirror(new("Workbox Test", source.Address, "board-test"),
            desktop.Queries, desktop.Transitions, () => status.Unreachable("Workbox Test", "Board connection closed"));
        var sub = desktop.Topics.Subscribe("machines", null, _ =>
        {
            clock.Start();
        }, (_, json) =>
        {
            var item = JsonSerializer.Deserialize(json, SessionsJson.Default.MachineItem)!;
            if (item.Name == mirror.Name && item.Reachable) mirror.Reconnect();
        });
        var unavailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        far.Probe = _ => { status.Unreachable("Workbox Test", "offline"); unavailable.TrySetResult(); return Task.CompletedTask; };
        desktop.Time.Advance(TimeSpan.FromSeconds(1));
        await unavailable.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.F.Update();
        var caught = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var board = desktop.Topics.Subscribe("board", null, _ => { }, (_, _) => caught.TrySetResult());
        far.Probe = _ => { status.Reachable("Workbox Test"); return Task.CompletedTask; };
        // Advance on the actual clock event, so PeriodicTimer never coalesces the test's ticks.
        TaskCompletionSource? tick = null;
        clock.Ticked += _ => tick?.TrySetResult();
        for (var i = 0; i < 15; i++)
        {
            tick = new(TaskCreationOptions.RunContinuationsAsynchronously);
            desktop.Time.Advance(TimeSpan.FromSeconds(1));
            await tick.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await caught.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(desktop.Page().Items);
        desktop.Topics.Unsubscribe(board);
        desktop.Topics.Unsubscribe(sub);
    }
}
