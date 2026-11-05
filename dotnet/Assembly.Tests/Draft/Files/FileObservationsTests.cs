using System.Diagnostics;
using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Infrastructure.Chat.Files;
using Assembly.Tests.Draft.Terminal;

namespace Assembly.Tests.Draft.Files;

/// <summary><c>filesChanged</c>: a folder made, deleted or renamed under an observed root, published once per window.</summary>
public sealed class FileObservationsTests
{
    /// <summary>Well past the window, so anything it would publish has been.</summary>
    private static readonly TimeSpan Settle = FileObservations.Window * 4;

    private static string[] Folders((string Topic, string? Key, string Json) published) =>
        [.. JsonDocument.Parse(published.Json).RootElement.GetProperty("folders").EnumerateArray().Select(folder => folder.GetString()!)];

    [Fact]
    public async Task Creating_a_file_publishes_once_after_the_debounce()
    {
        using var folder = new GitFolder("src/", "readme.md");
        var topics = new RecordingTopics();
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var observations = new FileObservations(topics, work);
        var lease = observations.Observe(folder.Root, "c1", CancellationToken.None).Lease;
        var clock = Stopwatch.StartNew();
        File.WriteAllText(Path.Combine(folder.Root, "src", "new.rs"), "");
        await topics.WaitFor(item => item.Topic == FileObservations.Topic);
        Assert.True(clock.Elapsed >= FileObservations.Window - TimeSpan.FromMilliseconds(20), $"published after {clock.Elapsed}");
        await Task.Delay(Settle);
        var published = Assert.Single(topics.Snapshot());
        Assert.Equal(lease, published.Key);
        Assert.Equal(["src"], Folders(published));
        observations.Release(lease, "c1");
    }

    [Fact]
    public async Task Writing_to_a_file_publishes_nothing()
    {
        using var folder = new GitFolder("readme.md");
        var topics = new RecordingTopics();
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var observations = new FileObservations(topics, work);
        var lease = observations.Observe(folder.Root, "c1", CancellationToken.None).Lease;
        File.AppendAllText(Path.Combine(folder.Root, "readme.md"), "more");
        await Task.Delay(Settle);
        Assert.Empty(topics.Snapshot());
        observations.Release(lease, "c1");
    }

    [Fact]
    public async Task A_change_inside_dot_git_publishes_nothing()
    {
        using var folder = new GitFolder("readme.md");
        var topics = new RecordingTopics();
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var observations = new FileObservations(topics, work);
        var lease = observations.Observe(folder.Root, "c1", CancellationToken.None).Lease;
        File.WriteAllText(Path.Combine(folder.Root, ".git", "probe"), "");
        Directory.CreateDirectory(Path.Combine(folder.Root, ".git", "refs", "probe"));
        await Task.Delay(Settle);
        Assert.Empty(topics.Snapshot());
        observations.Release(lease, "c1");
    }

    [Fact]
    public async Task A_change_in_another_dot_folder_is_published()
    {
        using var folder = new GitFolder(".github/workflows/", ".claude/", "readme.md");
        var topics = new RecordingTopics();
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var observations = new FileObservations(topics, work);
        var lease = observations.Observe(folder.Root, "c1", CancellationToken.None).Lease;
        File.WriteAllText(Path.Combine(folder.Root, ".github", "workflows", "ci.yml"), "");
        File.WriteAllText(Path.Combine(folder.Root, ".claude", "notes.md"), "");
        File.WriteAllText(Path.Combine(folder.Root, ".git", "probe"), "");
        await topics.WaitFor(item => item.Topic == FileObservations.Topic);
        await Task.Delay(Settle);
        var published = Assert.Single(topics.Snapshot());
        Assert.Equal([".claude", ".github/workflows"], Folders(published));
        observations.Release(lease, "c1");
    }

    [Fact]
    public async Task Releasing_the_lease_stops_publishing()
    {
        using var folder = new GitFolder("readme.md");
        var topics = new RecordingTopics();
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var observations = new FileObservations(topics, work);
        var lease = observations.Observe(folder.Root, "c1", CancellationToken.None).Lease;
        File.WriteAllText(Path.Combine(folder.Root, "during.md"), "");
        // Released while its window is open: the window ends with it.
        Assert.True(observations.Release(lease, "c1"));
        File.WriteAllText(Path.Combine(folder.Root, "after.md"), "");
        await Task.Delay(Settle);
        Assert.Empty(topics.Snapshot());
        Assert.False(observations.Holds(lease, "c1"));
    }

    [Fact]
    public async Task A_burst_names_each_folder_once()
    {
        using var folder = new GitFolder("a/", "b/", "readme.md");
        var topics = new RecordingTopics();
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var observations = new FileObservations(topics, work);
        var lease = observations.Observe(folder.Root, "c1", CancellationToken.None).Lease;
        File.WriteAllText(Path.Combine(folder.Root, "a", "1"), "");
        File.WriteAllText(Path.Combine(folder.Root, "a", "2"), "");
        File.WriteAllText(Path.Combine(folder.Root, "b", "1"), "");
        File.WriteAllText(Path.Combine(folder.Root, "top"), "");
        File.Move(Path.Combine(folder.Root, "a", "2"), Path.Combine(folder.Root, "b", "2"));
        await topics.WaitFor(item => item.Topic == FileObservations.Topic);
        await Task.Delay(Settle);
        var published = Assert.Single(topics.Snapshot());
        Assert.Equal(["", "a", "b"], Folders(published));
        observations.Release(lease, "c1");
    }

    [Fact]
    public async Task Only_the_connection_holding_a_lease_follows_it()
    {
        using var folder = new GitFolder("readme.md");
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var observations = new FileObservations(new RecordingTopics(), work);
        var lease = observations.Observe(folder.Root, "c1", CancellationToken.None).Lease;
        Assert.True(observations.Holds(lease, "c1"));
        Assert.False(observations.Holds(lease, "c2"));
        observations.Close("c1");
        Assert.False(observations.Holds(lease, "c1"));
    }
}
