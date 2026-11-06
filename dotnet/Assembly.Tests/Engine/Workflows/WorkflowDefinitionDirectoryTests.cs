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
    public void Invalid_definitions_and_missing_prompts_have_bounded_actual_errors_without_replacing_valid_neighbors()
    {
        Write("bad.yml", "name: Bad\nversion: 1\nsteps: {}");
        Write("missing.yml", "name: Missing\nsteps: {build: {agent: {provider: custom}, result: build, prompt: prompts/absent.md}}");
        Write("valid.yml", "name: Valid\nsteps: {check: {run: [\"true\"]}}");
        var page = WorkflowDefinitionDirectory.List(root);
        Assert.Equal(3, page.Items.Count);
        Assert.Contains("version", Assert.Single(page.Items[0].Errors)); Assert.Null(page.Items[0].Hash);
        Assert.Contains("steps.build.prompt", Assert.Single(page.Items[1].Errors)); Assert.Null(page.Items[1].Hash);
        Assert.Empty(page.Items[2].Errors); Assert.NotNull(page.Items[2].Hash);
        Assert.All(page.Items, item => Assert.True(JsonSerializer.SerializeToUtf8Bytes(item, WorkflowDefinitionReadJson.Default.WorkflowDefinitionDisplay).Length <= 32 * 1024));
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

    [Fact]
    public void Oversized_validation_errors_are_explicitly_shortened_within_the_display_budget()
    {
        Write("bad.yml", "name: Bad\n? " + new string('x', 5000) + "\n: true\nsteps: {}");
        var item = Assert.Single(WorkflowDefinitionDirectory.List(root).Items);
        var error = Assert.Single(item.Errors);
        Assert.EndsWith("…", error); Assert.True(Encoding.UTF8.GetByteCount(error) <= 2048);
        Assert.Null(item.Name); Assert.Null(item.Hash);
    }

    [Fact]
    public void Display_byte_budget_pages_before_two_hundred_without_losing_or_shortening_valid_names()
    {
        var name = new string('n', 20_000);
        foreach (var index in Enumerable.Range(0, 80)) Write($"n{index:D3}.yml", "name: " + name + "\nsteps: {}");
        List<string> paths = []; string? after = null; var pages = 0;
        do
        {
            var page = WorkflowDefinitionDirectory.List(root, after, 200); pages++;
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, WorkflowDefinitionReadJson.Default.WorkflowDefinitionPage).Length <= 1024 * 1024);
            Assert.All(page.Items, item => Assert.Equal(name, item.Name));
            paths.AddRange(page.Items.Select(item => item.Path)); after = page.Next;
        } while (after is not null);
        Assert.True(pages > 1);
        Assert.Equal(Enumerable.Range(0, 80).Select(index => $".assembly/workflows/n{index:D3}.yml"), paths);
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
