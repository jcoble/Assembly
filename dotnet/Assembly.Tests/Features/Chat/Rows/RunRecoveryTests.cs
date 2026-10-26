using Assembly.Domain.Events;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Infrastructure.Persistence;
using Assembly.Domain.Rows;
using Assembly.Application.Features.Chat;
using Assembly.Tests.Fixtures;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>A run the backend stopped in the middle of, closed on the next start from its saved rows alone.</summary>
public sealed class RunRecoveryTests : IDisposable
{
    private const long RestartedAt = 1_900_000_000_000;

    private readonly TempDatabase _db = new();
    private readonly AppDatabase _database;
    private readonly RowStore _store;

    public RunRecoveryTests()
    {
        _database = AppDatabase.Open(_db.Path);
        _store = RowStore.Open(_database);
    }

    [Theory]
    [InlineData("acp/tools.jsonl", "tool")]
    [InlineData("acp/text.jsonl", "delta")]
    [InlineData("handmade/thinking.jsonl", "thinking")]
    public void It_closes_what_the_run_left_open_as_a_lost_agent_would(string fixture, string cutAfterKind)
    {
        // Run the turn up to the first row of the kind, as if the backend died right after saving it.
        var events = FixtureReplay.Events(fixture);
        var converter = new RowConverter();
        AgentEvent? last = null;
        foreach (var agentEvent in events)
        {
            var batch = converter.Apply(agentEvent);
            _store.Commit("t", batch);
            last = agentEvent;
            if (batch.Rows.Any(row => row.Kind == cutAfterKind))
                break;
        }

        var lost = converter.Apply(new AgentLost(last!.At));
        var (session, run) = Assert.Single(_store.UnfinishedRuns());
        Assert.Equal("t", session);

        var recovered = RunRecovery.Close(_store.RunRows(session, run), RestartedAt);

        // The same closing rows, except no stats (the turn's tallies died with the backend) and its own error.
        var expected = lost.Rows.Where(row => row.Kind != RowKind.Stats).ToList();
        Assert.Equal(expected.Select(row => (row.RunId, row.ItemId, row.Kind)), recovered.Rows.Select(row => (row.RunId, row.ItemId, row.Kind)));
        foreach (var (want, got) in expected.Zip(recovered.Rows).Where(pair => pair.First.Kind is not RowKind.Error))
            Assert.Equal(want.Payload.GetRawText(), got.Payload.GetRawText());
        var error = recovered.Rows.Single(row => row.Kind == RowKind.Error);
        Assert.Equal(RunRecovery.Code, error.Payload.GetProperty("code").GetString());
        Assert.All(recovered.Rows, row => Assert.Equal(RestartedAt, row.CreatedAt));
    }

    [Fact]
    public void A_message_closed_from_its_deltas_replaces_them()
    {
        var converter = new RowConverter();
        foreach (var agentEvent in FixtureReplay.Events("acp/text.jsonl"))
        {
            var batch = converter.Apply(agentEvent);
            _store.Commit("t", batch);
            if (batch.Rows.Any(row => row.Kind == RowKind.Delta))
                break;
        }

        var (session, run) = Assert.Single(_store.UnfinishedRuns());
        _store.Commit(session, RunRecovery.Close(_store.RunRows(session, run), RestartedAt));
        var rows = _store.RunRows(session, run);
        Assert.DoesNotContain(rows, row => row.Kind == RowKind.Delta);
        Assert.Contains(rows, row => row.Kind == RowKind.Message);
    }

    [Fact]
    public void A_card_left_open_is_cancelled_so_it_no_longer_offers_its_buttons()
    {
        var converter = new RowConverter();
        var events = FixtureReplay.Events("acp/tools.jsonl").Take(5).ToList();
        foreach (var agentEvent in events)
            _store.Commit("t", converter.Apply(agentEvent));
        var options = new[] { new AskOption("allow", "Allow", "allow_once") };
        _store.Commit("t", converter.Apply(new AskChanged(events[^1].At, "7", new AskPayload("permission", "open", "Run ls", null, null, options, null, null))));
        var (session, run) = Assert.Single(_store.UnfinishedRuns());

        var recovered = RunRecovery.Close(_store.RunRows(session, run), RestartedAt);

        var card = Assert.Single(recovered.Rows, row => row.Kind == RowKind.Ask);
        Assert.Equal("ask:7", card.ItemId);
        Assert.Equal("cancelled", card.Payload.GetProperty("state").GetString());
        Assert.Equal("Run ls", card.Payload.GetProperty("title").GetString());
    }

    [Fact]
    public void Once_closed_a_run_is_no_longer_unfinished()
    {
        var converter = new RowConverter();
        foreach (var agentEvent in FixtureReplay.Events("acp/tools.jsonl").Take(5))
            _store.Commit("t", converter.Apply(agentEvent));
        var (session, run) = Assert.Single(_store.UnfinishedRuns());

        _store.Commit(session, RunRecovery.Close(_store.RunRows(session, run), RestartedAt));
        Assert.Empty(_store.UnfinishedRuns());
    }

    [Fact]
    public void Finished_runs_are_left_alone()
    {
        var converter = new RowConverter();
        foreach (var agentEvent in FixtureReplay.Events("acp/tools.jsonl"))
            _store.Commit("t", converter.Apply(agentEvent));

        Assert.Empty(_store.UnfinishedRuns());
    }

    [Fact]
    public void Only_runs_on_sessions_that_run_here_are_closed()
    {
        foreach (var session in new[] { "here", "remote" })
        {
            var converter = new RowConverter();
            foreach (var agentEvent in FixtureReplay.Events("acp/tools.jsonl").Take(5))
                _store.Commit(session, converter.Apply(agentEvent));
        }
        using var hub = new LiveHub(_store);

        var closed = RunRecovery.CloseUnfinished(_store, hub, RestartedAt, runsHere: session => session == "here");

        Assert.Equal(1, closed);
        Assert.Equal("remote", Assert.Single(_store.UnfinishedRuns()).SessionId);
    }

    [Fact]
    public void Background_tasks_left_running_after_their_run_ended_are_stopped()
    {
        var converter = new RowConverter();
        _store.Commit("t", converter.Apply(new TurnStarted(1, "run-1", "start it")));
        _store.Commit("t", converter.Apply(new TaskChanged(2, "a", "sleep 15", "running", CanStop: true, ToolCallId: null)));
        _store.Commit("t", converter.Apply(new TaskChanged(3, "b", "sleep 1", "running", CanStop: true, ToolCallId: null)));
        _store.Commit("t", converter.Apply(new TaskChanged(4, "b", "sleep 1", "completed", CanStop: false, ToolCallId: null)));
        _store.Commit("t", converter.Apply(new TurnEnded(5, "end_turn", null)));
        using var hub = new LiveHub(_store);

        RunRecovery.CloseUnfinished(_store, hub, RestartedAt, runsHere: _ => true);

        var task = _store.RowsAfter("t", 0).Last();
        Assert.Equal(("run-1", "task:a", RestartedAt), (task.RunId, task.ItemId, task.CreatedAt));
        Assert.Equal("stopped", task.Payload.GetProperty("state").GetString());
        Assert.Empty(_store.RunningItems(RowKind.Task));
    }

    public void Dispose()
    {
        _database.Dispose();
        _db.Dispose();
    }
}
