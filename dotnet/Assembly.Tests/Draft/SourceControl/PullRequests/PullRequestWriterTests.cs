using Assembly.Infrastructure.Git;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
using Assembly.Application.Common.GitHub;
using Assembly.Application.Features.Chat.SourceControl.PullRequests;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class PullRequestWriterTests
{
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Base = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    // Pins: g9#13, g9#14
    [Fact]
    public async Task Create_pushes_then_creates_with_exact_body_and_returns_the_number()
    {
        using var f = await Fixture.Create();
        f.Gh.Create = (args, input) =>
        {
            Assert.Equal(1, f.Git.Pushes);
            Assert.Contains("--draft", args); Assert.Equal("description\nsecond line", input);
            return new(0, "https://github.com/owner/repo/pull/7\n", "");
        };
        var created = await f.Writer.CreateAsync(f.Repo.Root, "main", "Title", "description\nsecond line", true, default);
        Assert.Equal(7, created.Number); Assert.Equal("main", created.Head);
        Assert.Equal(1, f.Gh.Writes);
    }
    [Fact]
    public async Task A_failed_push_creates_nothing()
    {
        using var f = await Fixture.Create(); f.Git.PushFails = true;
        await Assert.ThrowsAsync<CallFailed>(() => f.Writer.CreateAsync(f.Repo.Root, "main", "Title", "Body", false, default));
        Assert.Equal(0, f.Gh.Writes);
    }
    // Pins: g9#21, g9#23
    [Fact]
    public async Task A_moved_head_refuses_review_before_posting()
    {
        using var f = await Fixture.Create();
        await Assert.ThrowsAsync<CallFailed>(() => f.Writer.ReviewAsync(f.Repo.Root, 7, Base, "Review", [], default));
        Assert.Equal(0, f.Gh.Writes);
    }
    [Theory]
    [InlineData("code.cs", 99, "RIGHT")]
    [InlineData("other.cs", 1, "RIGHT")]
    [InlineData("code.cs", 1, "LEFT")]
    public async Task A_line_outside_the_patch_is_refused(string path, int line, string side)
    {
        using var f = await Fixture.Create();
        await Assert.ThrowsAsync<CallFailed>(() => f.Writer.ReviewAsync(f.Repo.Root, 7, Head, "Review", [new(path, line, side, "Line")], default));
        Assert.Equal(0, f.Gh.Writes);
    }
    [Fact]
    public async Task A_review_is_a_COMMENT_event_at_actual_head_and_source_line()
    {
        using var f = await Fixture.Create();
        await f.Writer.ReviewAsync(f.Repo.Root, 7, Head, " Overall ", [new("code.cs", 1, "RIGHT", " Line ")], default);
        using var body = JsonDocument.Parse(f.Gh.Input!);
        Assert.Equal("COMMENT", body.RootElement.GetProperty("event").GetString());
        Assert.Equal(Head, body.RootElement.GetProperty("commit_id").GetString());
        var comment = Assert.Single(body.RootElement.GetProperty("comments").EnumerateArray());
        Assert.Equal(1, comment.GetProperty("line").GetInt32());
        Assert.Equal("Line", comment.GetProperty("body").GetString());
        Assert.Equal(1, f.Gh.Writes);
    }
    // Pins: g9#24
    [Theory]
    [InlineData(true, "MERGEABLE", Base)]
    [InlineData(false, "CONFLICTING", Base)]
    [InlineData(false, "MERGEABLE", Head)]
    public async Task Merge_refuses_draft_unmergeable_or_moved_base(bool draft, string mergeable, string expectedBase)
    {
        using var f = await Fixture.Create(); f.Gh.Draft = draft; f.Gh.Mergeable = mergeable;
        await Assert.ThrowsAsync<CallFailed>(() => f.Writer.MergeAsync(f.Repo.Root, 7, "squash", Head, expectedBase, default));
        Assert.Equal(0, f.Gh.Writes);
    }
    [Fact]
    public async Task Merge_requires_a_positive_merged_response()
    {
        using var f = await Fixture.Create(); f.Gh.WriteResult = new(0, """{"merged":false}""", "");
        await Assert.ThrowsAsync<CallFailed>(() => f.Writer.MergeAsync(f.Repo.Root, 7, "squash", Head, Base, default));
        Assert.Equal(1, f.Gh.Writes);
    }
    [Fact]
    public async Task A_lost_write_is_propagated_once_and_a_nonzero_exit_is_a_failure()
    {
        using var f = await Fixture.Create(); f.Gh.Lost = true;
        await Assert.ThrowsAsync<GhInterrupted>(() => f.Writer.CommentAsync(f.Repo.Root, 7, Head, "Body", default));
        Assert.Equal(1, f.Gh.Writes);
        f.Gh.Lost = false; f.Gh.WriteResult = new(1, "", "Refused");
        var failure = await Assert.ThrowsAsync<CallFailed>(() => f.Writer.CommentAsync(f.Repo.Root, 7, Head, "Body", default));
        Assert.Contains("Refused", failure.Message); Assert.Equal(2, f.Gh.Writes);
    }
    [Fact]
    public async Task A_successful_exit_without_a_write_confirmation_is_unconfirmed_not_cancelled()
    {
        using var f = await Fixture.Create(); f.Gh.WriteResult = new(0, "not JSON", "");
        await Assert.ThrowsAsync<GhUnconfirmed>(() => f.Writer.CommentAsync(f.Repo.Root, 7, Head, "Body", default));
        Assert.Equal(1, f.Gh.Writes);
    }
    private sealed class Fixture : IDisposable
    {
        public GitHistoryTestRepository Repo { get; } = new();
        public FakeGh Gh { get; } = new();
        public RecordingGit Git { get; private set; } = null!;
        public PullRequestWriter Writer { get; private set; } = null!;
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); await f.Repo.InitializeAsync(); await f.Repo.Commit("Initial");
            await f.Repo.Run("remote", "add", "origin", "https://github.com/owner/repo.git");
            f.Git = new(f.Repo.Git);
            f.Writer = new(f.Git, f.Gh, new(f.Git, f.Gh, f.Repo.Root, new GitTextPatch(f.Git))); return f;
        }
        public void Dispose() => Repo.Dispose();
    }
    private sealed class RecordingGit(IGit inner) : IGit
    {
        public int Pushes; public bool PushFails;
        public Task<GitRun> RunAsync(string root, IReadOnlyList<string> args, string? input, TimeSpan? limit, CancellationToken ct)
        {
            if (args[0] != "push") return inner.RunAsync(root, args, input, limit, ct);
            Pushes++; return Task.FromResult(PushFails ? new GitRun(1, "", "push refused") : new GitRun(0, "", ""));
        }
        public IAsyncEnumerable<string> ReadAsync(string root, IReadOnlyList<string> args, IReadOnlyList<string>? input, CancellationToken ct) => inner.ReadAsync(root, args, input, ct);
    }
    private sealed class FakeGh : IGh
    {
        public int Writes; public bool Draft; public string Mergeable = "MERGEABLE"; public bool Lost; public string? Input;
        public Func<IReadOnlyList<string>, string?, GhRun>? Create;
        public GhRun WriteResult = new(0, """{"html_url":"https://github.com/owner/repo/pull/7#issuecomment-1","merged":true}""", "");
        public Task<GhRun> RunAsync(string folder, IReadOnlyList<string> args, string? input, TimeSpan limit, CancellationToken ct)
        {
            if (args.Contains("POST") || args.Contains("PUT") || args.Take(2).SequenceEqual(new[] { "pr", "create" }))
            {
                Writes++; Input = input;
                if (Lost) throw new GhInterrupted("lost", new IOException("fixture"), ct);
                return Task.FromResult(Create is null ? WriteResult : Create(args, input));
            }
            var text = args[0] == "pr" ? JsonSerializer.Serialize(new { number = 7, state = "OPEN", isDraft = Draft, mergeable = Mergeable, headRefOid = Head, baseRefOid = Base, url = "https://github.com/owner/repo/pull/7" })
                : args[1].Contains("/files?") ? """[{"filename":"code.cs","patch":"@@ -0,0 +1 @@\n+added"}]""" : "[]";
            return Task.FromResult(new GhRun(0, text, ""));
        }
    }
}
