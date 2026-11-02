using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Effects;
using Assembly.Engine.Workflows.Workspaces;
using Assembly.Infrastructure.Engine.Workflows;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Infrastructure.Engine.Workflows;

public sealed class WorkflowGitTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("a40-workflow-git-").FullName;
    private string Repo => Path.Combine(_root, "source repo");
    private string Managed => Path.Combine(_root, "managed");
    private string Proof => Path.Combine(_root, "proof");
    private readonly string _run = Guid.NewGuid().ToString("N");
    private readonly GitCli _git = new();

    [Fact]
    public async Task Crash_after_git_creation_reconciles_one_allocation_and_preserves_proof()
    {
        var i = await Intent(); var effect = Effect(i); var host = new WorkflowWorkspace(Managed, Proof);
        var first = await host.PrepareAsync(effect, false, default);
        Assert.Null(Data(first).Error);
        await File.WriteAllTextAsync(Path.Combine(i.ProofDir, "evidence.txt"), "keep");
        var replay = await new WorkflowWorkspace(Managed, Proof).PrepareAsync(effect with { Attempt = 2 }, true, default);
        Assert.Equal(Data(first), Data(replay));
        Assert.Equal(1, (await Git(Repo, "worktree", "list", "--porcelain")).Split("worktree " + i.Path).Length - 1);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(i.ProofDir, "evidence.txt")));
    }

    [Fact]
    public async Task New_steps_get_distinct_paths_on_dependency_head()
    {
        var anchor = await Intent(); var host = new WorkflowWorkspace(Managed, Proof);
        Assert.Null(Data(await host.PrepareAsync(Effect(anchor), false, default)).Error);
        await File.WriteAllTextAsync(Path.Combine(anchor.Path, "build.txt"), "checked build");
        await Git(anchor.Path, "add", "."); await Git(anchor.Path, "commit", "-m", "build");
        var head = await Git(anchor.Path, "rev-parse", "HEAD");
        var a = Step(anchor, head); var b = Step(anchor, head);
        var ar = Data(await host.PrepareAsync(Effect(a), false, default));
        var br = Data(await host.PrepareAsync(Effect(b), false, default));
        Assert.Null(ar.Error); Assert.Null(br.Error); Assert.NotEqual(ar.Path, br.Path);
        Assert.Equal(head, ar.BaseHead); Assert.Equal(head, br.Head);
        Assert.True(File.Exists(Path.Combine(ar.Path, "build.txt")));
        Assert.True(File.Exists(Path.Combine(br.Path, "build.txt")));
    }

    [Fact]
    public async Task Parent_mode_never_owns_or_deletes_owner_checkout()
    {
        var i = await Intent(); i = i with { Owned = false, Path = Repo, Branch = "" };
        var before = await Git(Repo, "worktree", "list", "--porcelain");
        var result = Data(await new WorkflowWorkspace(Managed, Proof).PrepareAsync(Effect(i), false, default));
        Assert.Null(result.Error); Assert.Equal(Repo, result.Path);
        Assert.Equal(before, await Git(Repo, "worktree", "list", "--porcelain"));
        Assert.False(Directory.Exists(Managed)); Assert.True(File.Exists(Path.Combine(Repo, "file.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unrelated_existing_directory_is_not_adopted(bool proof)
    {
        var i = await Intent(); var path = proof ? i.ProofDir : i.Path;
        Directory.CreateDirectory(path); await File.WriteAllTextAsync(Path.Combine(path, "precious"), "keep");
        var result = Data(await new WorkflowWorkspace(Managed, Proof).PrepareAsync(Effect(i), true, default));
        Assert.NotNull(result.Error); Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(path, "precious")));
        Assert.Single((await Git(Repo, "worktree", "list", "--porcelain")).Split("worktree ", StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Invalid_base_is_a_visible_setup_failure_and_caller_cancellation_is_preserved()
    {
        var i = await Intent(); var host = new WorkflowWorkspace(Managed, Proof);
        var result = await host.PrepareAsync(Effect(i with { BaseHead = new string('f', 40) }), false, default);
        Assert.Equal(EffectDisposition.Confirmed, result.Disposition); Assert.NotNull(Data(result).Error);
        Assert.False(Directory.Exists(Managed));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.PrepareAsync(Effect(i), false, stop.Token));
    }

    private WorkspaceIntent Step(WorkspaceIntent anchor, string head)
    {
        var step = Guid.NewGuid().ToString("N");
        return anchor with
        {
            AllocationId = Guid.NewGuid().ToString("N"),
            StepId = step,
            BaseHead = head,
            Path = Path.Combine(Path.GetDirectoryName(anchor.Path)!, step + "-0"),
            Branch = anchor.Branch + "-" + step[..8] + "-0"
        };
    }
    private async Task<WorkspaceIntent> Intent()
    {
        Directory.CreateDirectory(Repo);
        await Git(Repo, "init"); await Git(Repo, "config", "user.name", "Workflow Fixture");
        await Git(Repo, "config", "user.email", "workflow@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(Repo, "file.txt"), "base");
        await Git(Repo, "add", "."); await Git(Repo, "commit", "-m", "base");
        return new(_run, null, 0, Guid.NewGuid().ToString("N"), Repo,
            Path.Combine(Managed, "source-repo", "run-" + _run, "anchor"), "run-" + _run[..8],
            await Git(Repo, "rev-parse", "HEAD"), true, Path.Combine(Proof, Guid.NewGuid().ToString("N")), "definition-hash");
    }
    private static Effect Effect(WorkspaceIntent i) => new(Guid.NewGuid().ToString("N"), "workflow_workspace", null, i.RunId,
        JsonSerializer.SerializeToElement(i, EngineJson.Default.WorkspaceIntent), 1);
    private static WorkspacePrepared Data(EffectReceipt r) => r.Data.Deserialize(EngineJson.Default.WorkspacePrepared)!;
    private async Task<string> Git(string folder, params string[] args)
    {
        List<string> values = []; await foreach (var value in _git.ReadAsync(folder, args, null, default)) values.Add(value);
        return string.Join('\0', values).TrimEnd('\r', '\n');
    }
    public void Dispose() => Directory.Delete(_root, true);
}
