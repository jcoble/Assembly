using Assembly.Application.Features.Chat.SourceControl;

namespace Assembly.Tests.Draft.SourceControl;

/// <summary>Reads the branch and changed paths from git's NUL-separated status format.</summary>
public sealed class GitStatusPorcelainTests
{
    // Pins: scm#9, scm#10
    [Fact]
    public void Branch_upstream_and_all_entry_types_keep_their_paths_and_letters()
    {
        var head = new string('a', 40);
        var status = GitStatusPorcelain.Parse(
            $"# branch.oid {head}\0# branch.head feature/x\0# branch.upstream origin/feature/x\0# branch.ab +2 -1\0" +
            "1 M. N... 100644 100644 100644 a b staged file\0" +
            "1 MM N... 100644 100644 100644 a b edited again\0" +
            "2 R. N... 100644 100644 100644 a b R100 new name\0old name\0" +
            "u UU N... 100644 100644 100644 100644 a b c merge file\0" +
            "? new folder/untracked file\0! ignored\0");
        Assert.Equal("feature/x", status.Branch);
        Assert.Equal(head, status.Head);
        Assert.True(status.HasUpstream);
        Assert.Equal(2, status.Ahead);
        Assert.Equal(1, status.Behind);
        Assert.Equal(5, status.TotalFiles);
        Assert.Equal(new ChangedFile[]
        {
            new("staged file", null, 'M', '.'),
            new("edited again", null, 'M', 'M'),
            new("new name", "old name", 'R', '.'),
            new("merge file", null, 'U', 'U'),
            new("new folder/untracked file", null, '?', '?')
        }, status.Files);
    }

    // Pins: scm#10
    [Fact]
    public void Copy_keeps_its_original_path()
    {
        var file = Assert.Single(GitStatusPorcelain.Parse(
            "2 C. N... 100644 100644 100644 a b C100 copied file\0original file\0").Files);
        Assert.Equal(new ChangedFile("copied file", "original file", 'C', '.'), file);
    }

    // Pins: scm#9
    [Fact]
    public void Detached_head_has_no_branch_or_upstream()
    {
        var head = new string('b', 40);
        var status = GitStatusPorcelain.Parse($"# branch.oid {head}\0# branch.head (detached)\0");
        Assert.Null(status.Branch);
        Assert.Equal(head, status.Head);
        Assert.False(status.HasUpstream);
        Assert.Equal(0, status.Ahead);
        Assert.Equal(0, status.Behind);
    }

    // Pins: scm#9
    [Fact]
    public void Initial_branch_has_no_commit()
    {
        var status = GitStatusPorcelain.Parse("# branch.oid (initial)\0# branch.head main\0");
        Assert.Equal("main", status.Branch);
        Assert.Null(status.Head);
        Assert.False(status.HasUpstream);
        Assert.Equal(0, status.Ahead);
        Assert.Equal(0, status.Behind);
    }

    // Pins: scm#11
    [Fact]
    public void Caps_the_files_but_counts_every_entry()
    {
        var status = GitStatusPorcelain.Parse(string.Concat(Enumerable.Range(0, 1005).Select(i => $"? file {i}\0")));
        Assert.Equal(1000, status.Files.Count);
        Assert.Equal(1005, status.TotalFiles);
        Assert.Equal("file 999", status.Files[^1].Path);
    }

    // Pins: scm#10
    [Fact]
    public void Every_unmerged_entry_reads_as_a_conflict_on_both_sides()
    {
        var status = GitStatusPorcelain.Parse("u AA N... 100644 100644 100644 100644 a b c both added\0");
        Assert.Equal(new ChangedFile("both added", null, 'U', 'U'), Assert.Single(status.Files));
    }
}
