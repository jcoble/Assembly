using Assembly.Infrastructure.Engine.Workflows;
using System.Text.Json;
using Assembly.Engine.Workflows.Definitions;
namespace Assembly.Tests.Engine.Workflows;

public sealed class WorkflowLoaderTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "assembly-workflow-" + Guid.NewGuid().ToString("N"));
    const string Basic = "name: Test\nsteps:\n  build: {agent: {provider: custom-launcher}, result: build, prompt: prompts/build.md}\n";
    public WorkflowLoaderTests() { Directory.CreateDirectory(Path.Combine(root, ".assembly/workflows/prompts")); File.WriteAllText(Path.Combine(root, ".assembly/workflows/prompts/build.md"), "first"); }
    WorkflowSnapshot Load(string yaml = Basic) { File.WriteAllText(Path.Combine(root, ".assembly/workflows/test.yml"), yaml); return WorkflowLoader.Load(root, ".assembly/workflows/test.yml"); }
    [Fact]
    public void Defaults_and_immutable_hash_cover_full_prompt()
    {
        var a = Load();
        Assert.Equal((3L, 60L, 5L), (a.Definition.Limits.MaxSessions, a.Definition.Limits.StepTimeoutMinutes, a.Definition.Limits.MaxRounds));
        var step = a.Definition.Steps["build"];
        Assert.Equal(("new", true, 1L, "custom-launcher", (string?)null, (string?)null), (step.Workspace, step.Required, step.MaxAttempts, step.Provider, step.Model, step.Permission));
        File.WriteAllText(Path.Combine(root, ".assembly/workflows/prompts/build.md"), new string('x', 1_100_000));
        var b = Load();
        Assert.NotEqual(a.Hash, b.Hash); Assert.Equal("first", a.Prompts["prompts/build.md"]); Assert.Equal(1_100_000, b.Prompts["prompts/build.md"].Length);
        Assert.Equal(b.Hash, Load().Hash);
        Assert.Equal(1, a.SchemaVersion);
        var json = JsonSerializer.Serialize(a, WorkflowJson.Default.WorkflowSnapshot);
        Assert.Equal(a.Hash, JsonSerializer.Deserialize(json, WorkflowJson.Default.WorkflowSnapshot)!.Hash);
    }
    [Fact]
    public void Legacy_display_bytes_and_hash_inputs_remain_compatible_when_new_fields_are_absent()
    {
        var legacy = Load("name: A\nsteps: {g: {gate: {}, display: {label: Approved, stage: approval}}}");
        var reordered = Load("steps: {g: {display: {stage: approval, label: Approved}, gate: {}}}\nname: A");
        var json = JsonSerializer.SerializeToElement(legacy.Definition, WorkflowJson.Default.WorkflowDefinition);
        Assert.Equal("{\"label\":\"Approved\",\"stage\":\"approval\"}", json.GetProperty("steps").GetProperty("g").GetProperty("display").GetRawText());
        Assert.Equal(legacy.Hash, reordered.Hash);
        var empty = Load("name: A\nsteps: {g: {gate: {}, display: {}}}");
        var emptyJson = JsonSerializer.SerializeToElement(empty.Definition, WorkflowJson.Default.WorkflowDefinition);
        Assert.Equal("{\"label\":null,\"stage\":null}", emptyJson.GetProperty("steps").GetProperty("g").GetProperty("display").GetRawText());
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(legacy, WorkflowJson.Default.WorkflowSnapshot), WorkflowJson.Default.WorkflowSnapshot)!;
        Assert.Equal(legacy.Definition.Steps["g"].Display, restored.Definition.Steps["g"].Display);
        Assert.Equal(legacy.Hash, WorkflowParser.Parse(new(restored.Yaml, restored.Prompts)).Hash);
    }
    [Fact]
    public void Symlink_escape_and_cancellation_refuse()
    {
        var outside = Path.Combine(Path.GetTempPath(), "assembly-outside-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(outside, "outside");
        try { File.CreateSymbolicLink(Path.Combine(root, ".assembly/workflows/prompts/escape.md"), outside); Assert.Contains("prompt", Assert.Throws<WorkflowFieldException>(() => Load(Basic.Replace("build.md", "escape.md"))).Message); }
        finally { File.Delete(outside); }
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => WorkflowLoader.Load(root, ".assembly/workflows/test.yml", stop.Token));
    }
    [Fact]
    public void Required_defaults_reserved_inputs_and_input_hash_are_start_time()
    {
        var definition = Load("name: A\ninputs: {request: {required: true}, flag: {default: false}}\nsteps: {approval: {gate: {nested: [1, true, null]}}}").Definition;
        var empty = JsonDocument.Parse("{}").RootElement;
        Assert.Contains("inputs.request", Assert.Throws<WorkflowFieldException>(() => WorkflowParser.ResolveInputs(definition, empty, empty)).Message);
        var supplied = JsonDocument.Parse("""{"request":"fix","task":"spoof","cwd":"spoof"}""").RootElement;
        var resolved = JsonDocument.Parse("""{"task":{"key":"TSK-1"},"cwd":"/real"}""").RootElement;
        var snapshot = WorkflowParser.ResolveInputs(definition, supplied, resolved);
        Assert.False(snapshot.Values.GetProperty("flag").GetBoolean());
        Assert.Equal("/real", snapshot.Values.GetProperty("cwd").GetString());
        Assert.Equal("TSK-1", snapshot.Values.GetProperty("task").GetProperty("key").GetString());
    }
    [Theory]
    [InlineData("dev-pipeline.yml", 14, 6)]
    [InlineData("spec-to-pr.yml", 9, 4)]
    public void Actual_old_pipeline_fixtures_load_without_executing_commands(string file, int count, int rounds)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Engine/Workflows/Fixtures/old-d5f5fe68");
        foreach (var source in Directory.EnumerateFiles(Path.Combine(fixture, "workflows"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, ".assembly/workflows", Path.GetRelativePath(Path.Combine(fixture, "workflows"), source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, true);
        }
        var snapshot = WorkflowLoader.Load(root, ".assembly/workflows/" + file);
        Assert.Equal(count, snapshot.Definition.Steps.Count);
        Assert.Equal(rounds, snapshot.Definition.Limits.MaxRounds);
        Assert.Contains("gh pr", snapshot.Definition.Steps["pr"].Commands[1]);
        Assert.Contains("${{ run.pr_body }}", snapshot.Definition.Steps["pr"].Commands[1]);
        Assert.All(snapshot.Definition.Steps.Values.Where(s => s.Prompt is not null), s => Assert.True(snapshot.Prompts.ContainsKey(s.Prompt!)));
    }
    [Fact]
    public void Definition_hash_is_canonical_and_full_content_with_more_than_two_hundred_steps()
    {
        var a = Load("name: A\ninputs: {value: {default: {z: 1, a: [true, null]}}}\nsteps: {g: {gate: {z: 1, a: 2}}}");
        var b = Load("steps: {g: {gate: {a: 2, z: 1}}}\ninputs: {value: {default: {a: [true, null], z: 1}}}\nname: A");
        Assert.Equal(a.Hash, b.Hash);
        var many = "name: Many\nsteps:\n" + string.Concat(Enumerable.Range(0, 225).Select(i => $"  g{i}: {{gate: {{context: [true, null, 12, \"yes\"]}}}}\n"));
        Assert.Equal(225, Load(many).Definition.Steps.Count);
    }
    [Fact]
    public void Workflow_file_symlink_cannot_escape_workflow_directory()
    {
        var outside = Path.Combine(root, "outside.yml"); File.WriteAllText(outside, Basic);
        File.CreateSymbolicLink(Path.Combine(root, ".assembly/workflows/link.yml"), outside);
        Assert.Contains("path", Assert.Throws<WorkflowFieldException>(() => WorkflowLoader.Load(root, ".assembly/workflows/link.yml")).Message);
    }
    [Fact]
    public void Large_yaml_is_not_capped_and_required_input_hash_is_canonical()
    {
        var yaml = "name: Large\nsteps: {g: {gate: {context: " + new string('x', 300_000) + "}}}";
        Assert.Equal(300_000, Load(yaml).Definition.Steps["g"].Gate!.Value.GetProperty("context").GetString()!.Length);
        var definition = Load().Definition;
        using var a = JsonDocument.Parse("{\"object\":{\"a\":1,\"b\":2}}");
        using var b = JsonDocument.Parse("{\"object\":{\"b\":2,\"a\":1}}");
        using var empty = JsonDocument.Parse("{}");
        Assert.Equal(WorkflowParser.ResolveInputs(definition, a.RootElement, empty.RootElement).Hash,
            WorkflowParser.ResolveInputs(definition, b.RootElement, empty.RootElement).Hash);
    }
    public void Dispose() => Directory.Delete(root, true);
}
