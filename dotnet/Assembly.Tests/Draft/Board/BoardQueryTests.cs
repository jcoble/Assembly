using Assembly.Application.Features.Workflows;
using Assembly.Application.Features.Workflows.Contracts;
using Assembly.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Board;

public sealed class BoardQueryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    internal sealed class Fixture : IDisposable
    {
        private readonly TempDatabase _file = new();
        public TopicSet Topics { get; } = new();
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.FromUnixTimeMilliseconds(1000));
        public AppDatabase Db { get; }
        public WorkflowQueries Queries { get; }
        public WorkflowTransitions Transitions { get; }
        public Fixture()
        {
            Db = AppDatabase.Open(_file.Path, Time, Topics);
            WorkflowSchema.Open(Db);
            Queries = new(Db);
            Transitions = new(Db, Time);
        }
        public BoardUpdateArgs Report(string key = "task", string state = "running", BoardQuestionAsk? question = null,
            string[]? paths = null, BoardAnswerReceived? received = null) =>
            new(key, "A long real task title", state, "Measured progress", null, null, paths, question, received);
        public BoardUpdateResult Update(string key = "task", string state = "running", BoardQuestionAsk? question = null,
            string[]? paths = null, BoardAnswerReceived? received = null) =>
            Transitions.Update("session", "project", Report(key, state, question, paths, received));
        public BoardTaskResult Detail(string key = "task", string machine = "local", long? before = null, string? cursor = null) =>
            Queries.Task(new(machine, key, before, cursor), CancellationToken.None);
        public BoardPageResult Page(string? cursor = null, string? project = null, string? state = null) =>
            Queries.Page(new(project, state, cursor), CancellationToken.None);
        public BoardAnswerArgs Answer(string question = "q1", int generation = 1, string? key = null, BoardAnswerValue? value = null) =>
            new("local", "task", question, generation, key ?? Guid.NewGuid().ToString(), value ?? new("text", "Use the simple option"));
        public BoardQuestionAsk Ask(string key = "q1", int generation = 0, string kind = "text") =>
            new(key, generation, kind, "Which option?", "Use the simple option");
        public void Dispose() { Db.Dispose(); _file.Dispose(); }
    }

    // Pins: board#5
    [Fact]
    public void PageCrossesAttentionGroupsAndEqualTimestamps()
    {
        using var f = new Fixture();
        for (var i = 0; i < 85; i++)
            f.Update($"task-{i:000}", i % 3 == 0 ? "completed" : "running", i % 3 == 1 ? f.Ask() : null);
        var seen = new List<BoardSummary>();
        string? cursor = null;
        do
        {
            var page = f.Page(cursor);
            Assert.Equal(85, page.Counts.Total);
            Assert.Equal(page.Counts.Total, page.Counts.Running + page.Counts.NeedsYou + page.Counts.Terminal);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(85, seen.Count);
        Assert.Equal(85, seen.Select(x => x.Task.TaskKey).Distinct().Count());
        Assert.Equal(seen.OrderBy(x => x.AttentionGroup).ThenByDescending(x => x.Task.UpdatedAt)
            .ThenBy(x => x.Task.MachineId, StringComparer.Ordinal).ThenBy(x => x.Task.TaskKey, StringComparer.Ordinal), seen);
    }

    [Fact]
    public void EmptyFilteredPageRetainsCounts()
    {
        using var f = new Fixture();
        f.Update();
        var cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            """{"attentionGroup":2,"updatedAt":0,"machineId":"zz","taskKey":"zz"}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var page = f.Page(cursor, "project");
        Assert.Empty(page.Items);
        Assert.Equal(1, page.Counts.Total);
        Assert.Equal(0, f.Page(project: "absent").Counts.Total);
        Assert.Equal(0, f.Page(state: "failed").Counts.Total);
    }

    [Fact]
    public void QuestionTakesPriorityOverTerminalReport()
    {
        using var f = new Fixture();
        f.Update(state: "completed", question: f.Ask());
        Assert.Equal(0, Assert.Single(f.Page().Items).AttentionGroup);
        f.Transitions.Answer(f.Answer(), true);
        Assert.Equal(2, Assert.Single(f.Page().Items).AttentionGroup);
    }

    [Fact]
    public void PathEvidenceIsSqlDistinctAndUnknownIsNull()
    {
        using var f = new Fixture();
        f.Update();
        Assert.Null(f.Detail().Metrics.Files);
        f.Update(paths: []);
        Assert.Equal(0, f.Detail().Metrics.Files);
        f.Update(paths: ["a.cs", "a.cs", "b.cs"]);
        f.Update(paths: ["b.cs"]);
        Assert.Equal(2, f.Detail().Metrics.Files);
        Assert.Equal(2, Assert.Single(f.Page().Items).Metrics.Files);
        Assert.Null(f.Detail().Metrics.Tools);
        Assert.Null(f.Detail().Metrics.StepsDone);
    }

    [Fact]
    public void TerminalDurationFreezes()
    {
        using var f = new Fixture();
        f.Update();
        f.Time.Advance(TimeSpan.FromSeconds(3));
        f.Update(state: "completed");
        var end = f.Detail().Task.EndedAt;
        Assert.NotNull(end);
        Assert.Equal(4000, end);
        f.Time.Advance(TimeSpan.FromSeconds(4));
        f.Update(state: "completed");
        Assert.Equal(end, f.Detail().Task.EndedAt);
        f.Update();
        Assert.Null(f.Detail().Task.EndedAt);
        Assert.Equal(1000, f.Detail().Task.StartedAt);
    }

    [Fact]
    public void QuestionsAndFeedHaveIndependentContinuations()
    {
        using var f = new Fixture();
        for (var i = 0; i < 105; i++) f.Update(question: f.Ask($"q{i:000}"));
        var first = f.Detail();
        Assert.Equal(50, first.Questions.Length);
        Assert.Equal(100, first.Feed.Length);
        var second = f.Detail(before: first.NextBeforeSeq, cursor: first.NextQuestionCursor);
        Assert.Equal(50, second.Questions.Length);
        Assert.Equal(5, second.Feed.Length);
        var third = f.Detail(cursor: second.NextQuestionCursor);
        Assert.Equal(5, third.Questions.Length);
        Assert.Null(third.NextQuestionCursor);
        Assert.Null(second.NextBeforeSeq);
    }

    [Fact]
    public void PopulatedQueryPlans()
    {
        using var f = new Fixture();
        for (var i = 0; i < 85; i++) f.Update($"task-{i}", question: f.Ask(), paths: ["a", "a"]);
        var reads = new[]
        {
            (WorkflowQueries.PageSql, new global::Assembly.Application.Common.Database.SqlArg[] {
                new("$project", "project"), new("$state", null), new("$cg", null), new("$cu", null), new("$cm", null), new("$ck", null), new("$remote", "Workbox Test") }),
            (WorkflowQueries.TaskSql, new global::Assembly.Application.Common.Database.SqlArg[] {
                new("$machine", "local"), new("$task", "task-1"), new("$before", null), new("$qa", null), new("$qk", null) }),
            (WorkflowQueries.ChangesSql, new global::Assembly.Application.Common.Database.SqlArg[] { new("$after", 0) })
        };
        foreach (var (sql, args) in reads)
        {
            output.WriteLine(sql);
            foreach (var plan in f.Db.Query("EXPLAIN QUERY PLAN " + sql, r => r.Text(3), CancellationToken.None, args))
                output.WriteLine(plan);
        }
        Assert.Equal(85, f.Page().Counts.Total);
    }
}
