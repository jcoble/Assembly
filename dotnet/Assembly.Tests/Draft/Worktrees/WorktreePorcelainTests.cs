using Assembly.Application.Features.Worktrees;

namespace Assembly.Tests.Draft.Worktrees;

public sealed class WorktreePorcelainTests
{
    // Pins: scm#1, scm#2
    [Fact]
    public void Parses_branches_detached_and_git_marks_without_changing_paths()
    {
        var head = new string('a', 40);
        var rows = WorktreePorcelain.Parse(
            $"worktree /main\0HEAD {head}\0branch refs/heads/main\0\0" +
            $"worktree /linked with\na newline\0HEAD {head}\0branch refs/heads/feature/x\0\0" +
            $"worktree /detached\0HEAD {head}\0detached\0\0" +
            $"worktree /locked\0HEAD {head}\0branch refs/heads/locked\0locked on a disk\0\0" +
            $"worktree /gone\0HEAD {head}\0detached\0prunable folder gone\0\0");

        Assert.Equal(5, rows.Count);
        Assert.True(rows[0].IsMain);
        Assert.Equal("main", rows[0].Branch);
        Assert.Equal("/linked with\na newline", rows[1].Path);
        Assert.Equal("feature/x", rows[1].Branch);
        Assert.False(rows[1].IsMain);
        Assert.Null(rows[2].Branch);
        Assert.Equal(head, rows[2].Head);
        Assert.True(rows[3].Locked);
        Assert.True(rows[4].Prunable);
    }

    // Pins: scm#1
    [Fact]
    public void Parses_a_bare_main()
    {
        var row = Assert.Single(WorktreePorcelain.Parse("worktree /bare\0bare\0\0"));
        Assert.True(row.IsMain);
        Assert.True(row.IsBare);
        Assert.Null(row.Head);
        Assert.Null(row.Branch);
    }
}
