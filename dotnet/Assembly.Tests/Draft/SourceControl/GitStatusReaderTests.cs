using Assembly.Application.Common.Calls;
using Assembly.Application.Features.SourceControl;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.SourceControl;

/// <summary>Checks the status reader against real repositories without changing their index.</summary>
public sealed class GitStatusReaderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "assembly-status-" + Guid.NewGuid());
    private readonly GitCli _git = new();
    private string Root => Path.Combine(_temp, "main");
    private Task<string> Git(string folder, params string[] arguments) => _git.RunAsync(folder, arguments, CancellationToken.None);
    private Task<GitStatus> Read() => new GitStatusReader(_git).ReadAsync(Root, CancellationToken.None);

    private async Task Init()
    {
        Directory.CreateDirectory(Root);
        await Git(Root, "init", "-b", "main");
        await Git(Root, "config", "user.name", "Status Test");
        await Git(Root, "config", "user.email", "status@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(Root, "tracked"), "original content");
        await Git(Root, "add", "tracked");
        await Commit("First subject");
    }

    private Task<string> Commit(string subject) => Git(Root, "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", subject);

    // Pins: scm#9
    [Fact]
    public async Task Branch_and_head_read_without_an_upstream()
    {
        await Init();
        var status = await Read();
        Assert.Equal("main", status.Branch);
        Assert.Equal((await Git(Root, "rev-parse", "HEAD")).Trim(), status.Head);
        Assert.False(status.HasUpstream);
        Assert.Equal(0, status.Ahead);
        Assert.Equal(0, status.Behind);
        Assert.Empty(status.Files);
        Assert.Equal(0, status.TotalFiles);
    }

    // Pins: scm#9
    [Fact]
    public async Task Counts_commits_ahead_of_the_upstream()
    {
        await Init();
        var origin = Path.Combine(_temp, "origin");
        await Git(_temp, "init", "--bare", origin);
        await Git(Root, "remote", "add", "origin", origin);
        await Git(Root, "push", "-u", "origin", "main");
        await Commit("Local commit");
        var status = await Read();
        Assert.True(status.HasUpstream);
        Assert.Equal(1, status.Ahead);
        Assert.Equal(0, status.Behind);
    }

    // Pins: scm#10
    [Fact]
    public async Task Staged_then_edited_file_keeps_both_changes()
    {
        await Init();
        await File.WriteAllTextAsync(Path.Combine(Root, "tracked"), "staged content");
        await Git(Root, "add", "tracked");
        await File.WriteAllTextAsync(Path.Combine(Root, "tracked"), "edited again");
        Assert.Equal(new ChangedFile("tracked", null, 'M', 'M'), Assert.Single((await Read()).Files));
    }

    // Pins: scm#10
    [Fact]
    public async Task Untracked_folder_lists_its_individual_files()
    {
        await Init();
        Directory.CreateDirectory(Path.Combine(Root, "new folder"));
        await File.WriteAllTextAsync(Path.Combine(Root, "new folder", "new file"), "new content");
        Assert.Equal(new ChangedFile("new folder/new file", null, '?', '?'), Assert.Single((await Read()).Files));
    }

    // Pins: scm#10
    [Fact]
    public async Task Rename_keeps_the_original_path()
    {
        await Init();
        await Git(Root, "mv", "tracked", "renamed file");
        Assert.Equal(new ChangedFile("renamed file", "tracked", 'R', '.'), Assert.Single((await Read()).Files));
    }

    // Pins: scm#12
    [Fact]
    public async Task Non_repository_shows_gits_own_error()
    {
        Directory.CreateDirectory(Root);
        var error = await Assert.ThrowsAsync<CallFailed>(() => Read());
        Assert.Contains("not a git repository", error.Message);
    }

    // Pins: scm#12
    [Fact]
    public async Task Reading_does_not_refresh_the_index_when_only_mtime_changed()
    {
        await Init();
        var tracked = Path.Combine(Root, "tracked");
        var index = Path.Combine(Root, ".git", "index");
        var before = await File.ReadAllBytesAsync(index);
        File.SetLastWriteTimeUtc(tracked, File.GetLastWriteTimeUtc(tracked).AddMinutes(-10));
        Assert.Empty((await Read()).Files);
        Assert.Equal(before, await File.ReadAllBytesAsync(index));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }
}
