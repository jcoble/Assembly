using System.Text;
using System.Text.Json;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Infrastructure.Engine.Workflows;

namespace Assembly.Tests.Engine.Workflows;

public sealed class WorkflowDefinitionDirectoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "assembly-definition-directory-" + Guid.NewGuid().ToString("N"));
    public WorkflowDefinitionDirectoryTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Explicit_reads_use_ordinal_path_keysets_and_the_same_full_content_loader_hash()
    {
        Write("z.yml", "name: Last\nsteps: {check: {run: [\"true\"]}}");
        Write("a.yml", "name: First\nsteps: {build: {agent: {provider: custom}, result: build, prompt: prompts/build.md}}");
        Directory.CreateDirectory(Path.Combine(root, ".assembly/workflows/prompts"));
        File.WriteAllText(Path.Combine(root, ".assembly/workflows/prompts/build.md"), new string('x', 1_100_000));
        Write("ignored.yaml", "name: Not supported\nsteps: {}");
        Write("nested/ignored.yml", "name: Nested\nsteps: {}");
        var first = WorkflowDefinitionDirectory.List(root, null, 1);
        var item = Assert.Single(first.Items);
        Assert.Equal(".assembly/workflows/a.yml", item.Path); Assert.Equal("First", item.Name);
        Assert.Equal(WorkflowLoader.Load(root, item.Path).Hash, item.Hash); Assert.Empty(item.Errors);
        var second = WorkflowDefinitionDirectory.List(root, first.Next, 1);
        Assert.Equal(".assembly/workflows/z.yml", Assert.Single(second.Items).Path); Assert.Null(second.Next);
        File.WriteAllText(Path.Combine(root, ".assembly/workflows/prompts/build.md"), "changed prompt");
        Assert.NotEqual(item.Hash, WorkflowDefinitionDirectory.List(root, null, 1).Items[0].Hash);
    }

    [Fact]
    public void Directory_and_file_symlink_escapes_use_the_existing_loader_path_guards()
    {
        var outside = root + "-outside"; Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "private.yml"), "name: Outside\nsteps: {}");
        Directory.CreateDirectory(Path.Combine(root, ".assembly"));
        Directory.CreateSymbolicLink(Path.Combine(root, ".assembly/workflows"), outside);
        Assert.Equal("path", Assert.Throws<WorkflowFieldException>(() => WorkflowDefinitionDirectory.List(root)).Field);
        Directory.Delete(Path.Combine(root, ".assembly/workflows"));
        Directory.CreateDirectory(Path.Combine(root, ".assembly/workflows"));
        File.CreateSymbolicLink(Path.Combine(root, ".assembly/workflows/escape.yml"), Path.Combine(outside, "private.yml"));
        var item = Assert.Single(WorkflowDefinitionDirectory.List(root).Items);
        Assert.Null(item.Name); Assert.Null(item.Hash); Assert.Contains("path", Assert.Single(item.Errors));
    }

    [Fact]
    public void Missing_workflow_directory_is_empty_but_missing_or_implicit_project_roots_and_invalid_limits_refuse()
    {
        Assert.Empty(WorkflowDefinitionDirectory.List(root).Items);
        Assert.Throws<WorkflowFieldException>(() => WorkflowDefinitionDirectory.List(""));
        Assert.Throws<WorkflowFieldException>(() => WorkflowDefinitionDirectory.List("relative"));
        Assert.Throws<WorkflowFieldException>(() => WorkflowDefinitionDirectory.List(Path.Combine(root, "missing")));
        Assert.Throws<ArgumentException>(() => WorkflowDefinitionDirectory.List(root, null, 201));
        Assert.Throws<ArgumentException>(() => WorkflowDefinitionDirectory.List(root, "../outside.yml", 50));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => WorkflowDefinitionDirectory.List(root, null, 50, stop.Token));
    }

    [Fact]
    public void Two_hundred_result_and_page_budgets_do_not_change_valid_definition_bytes_or_invent_a_short_name()
    {
        foreach (var index in Enumerable.Range(0, 205).Reverse()) Write($"n{index:D3}.yml", "name: Item\nsteps: {}");
        var first = WorkflowDefinitionDirectory.List(root, null, 200);
        Assert.Equal(200, first.Items.Count); Assert.Equal(".assembly/workflows/n000.yml", first.Items[0].Path);
        Assert.Equal(5, WorkflowDefinitionDirectory.List(root, first.Next, 200).Items.Count);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(first, WorkflowDefinitionReadJson.Default.WorkflowDefinitionPage).Length <= 1024 * 1024);
        Write("a.yml", "name: " + new string('x', 40_000) + "\nsteps: {}");
        var huge = WorkflowDefinitionDirectory.List(root, null, 1).Items[0];
        Assert.Null(huge.Name); Assert.Equal(WorkflowLoader.Load(root, huge.Path).Hash, huge.Hash);
        Assert.Contains("display", Assert.Single(huge.Errors));
        Assert.True(Encoding.UTF8.GetByteCount(Assert.Single(huge.Errors)) <= 2048);
    }

    private void Write(string name, string yaml)
    {
        var path = Path.Combine(root, ".assembly/workflows", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, yaml);
    }
    public void Dispose()
    {
        Directory.Delete(root, true);
        if (Directory.Exists(root + "-outside")) Directory.Delete(root + "-outside", true);
    }
}
