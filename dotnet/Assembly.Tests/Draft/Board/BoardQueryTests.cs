using Assembly.Application.Features.Workflows;
using Assembly.Application.Features.Workflows.Contracts;
using Assembly.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Board;

public sealed class BoardQueryTests
{
    internal sealed class Fixture : IDisposable
    {
        private readonly TempDatabase _file = new();
        public string Folder => _file.Folder;
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
        public static BoardStep[] Steps() => [
            new(0, "Read the failing case", "read", "case.cs", "done", "changed", null, null,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(2), null, null, null, null, null),
            new(1, "Retry the case", "test", "dotnet test", "running", null, "Second attempt", 40,
                DateTimeOffset.UnixEpoch.AddSeconds(2), null, 2, 3, null, null, null),
            new(2, "Choose a scope", null, null, "waiting", "needs-you", "Which branch?", null,
                null, null, null, null, "scope", "issues:create", false),
            new(3, "File the result", null, null, "future", null, null, null,
                null, null, null, null, null, null, null)
        ];
        public void Dispose() { Db.Dispose(); _file.Dispose(); }
    }

    // Pins: board#5
    [Fact]
    public void TaskReturnsCurrentStepsAndMetrics()
    {
        using var f = new Fixture();
        var first = Fixture.Steps();
        f.Transitions.Update("session", "project", f.Report() with { Steps = first });
        var second = new[] { first[0], first[1] with { State = "failed" }, first[2] with { State = "skipped" } };
        f.Transitions.Update("session", "project", f.Report() with { Steps = second });
        var detail = f.Detail();
        Assert.Equal(second, detail.Steps);
        Assert.Equal(2, detail.Feed.Length);
        Assert.All(detail.Feed, e => Assert.Null(e.Steps));
        var json = System.Text.Json.JsonSerializer.Serialize(detail,
            global::Assembly.Infrastructure.Workflows.WorkflowJson.Default.BoardTaskResult);
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.All(parsed.RootElement.GetProperty("feed").EnumerateArray(), e => Assert.False(e.TryGetProperty("steps", out _)));
        Assert.Equal(new BoardMetrics(1, 1, 1, null, null), detail.Metrics);
        Assert.Equal(detail.Metrics, Assert.Single(f.Page().Items).Metrics);
        Assert.Equal(second, f.Detail(before: detail.Feed[^1].Seq).Steps);
        f.Transitions.Update("session", "project", f.Report() with { Steps = [] });
        Assert.Empty(f.Detail().Steps);
        Assert.Equal(0, f.Detail().Metrics.StepsDone);
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
}
