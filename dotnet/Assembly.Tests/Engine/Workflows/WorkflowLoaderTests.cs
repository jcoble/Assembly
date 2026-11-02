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
    [Theory]
    [InlineData("name: A\nname: B\nsteps: {}", "name")]
    [InlineData("name: A\nsteps: {}\n---\nname: B", "yaml")]
    [InlineData("name: &a A\nsteps: *a", "yaml")]
    [InlineData("name: !custom A\nsteps: {}", "yaml")]
    [InlineData("name: A\nversion: 1\nsteps: {}", "version")]
    [InlineData("name: A\nlimits: {max_sessions: 0}\nsteps: {}", "limits.max_sessions")]
    [InlineData("name: A\nlimits: {step_timeout_minutes: true}\nsteps: {}", "limits.step_timeout_minutes")]
    [InlineData("name: A\nsteps: {bad.id: {gate: yes}}", "steps.bad.id")]
    [InlineData("name: A\nsteps: {a: {gate: {}, needs: [b]}}", "steps.a.needs")]
    [InlineData("name: A\nsteps: {a: {gate: {}, needs: [b]}, b: {gate: {}, needs: [a]}}", "needs")]
    [InlineData("name: A\nsteps: {a: {gate: {}, mystery: true}}", "steps.a.mystery")]
    [InlineData("name: A\nsteps: {a: {agent: {provider: x, provider: y}}}", "steps.a.agent.provider")]
    [InlineData("name: A\npolicy: {commands: {alow: []}}\nsteps: {}", "policy.commands.alow")]
    public void Invalid_definitions_name_field(string yaml, string field) => Assert.Contains(field, Assert.Throws<WorkflowFieldException>(() => Load(yaml)).Message);
    [Fact]
    public void Nested_validation_and_parallel_build_lineage()
    {
        Assert.Contains("agent.model", Assert.Throws<WorkflowFieldException>(() => Load(Basic.Replace("provider: custom-launcher", "provider: custom-launcher, model: 5"))).Message);
        Assert.Contains("agent.extra", Assert.Throws<WorkflowFieldException>(() => Load(Basic.Replace("provider: custom-launcher", "provider: custom-launcher, extra: true"))).Message);
        Assert.Contains("prompt", Assert.Throws<WorkflowFieldException>(() => Load(Basic.Replace("prompts/build.md", "../outside.md"))).Message);
        Assert.Contains("agent", Assert.Throws<WorkflowFieldException>(() => Load(Basic + "  other: {agent: {provider: x}, result: build, prompt: prompts/build.md}\n")).Message);
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
    public void Review_of_and_on_fail_are_independent_validated_dependencies()
    {
        const string yaml = "name: Dual\nsteps:\n  build: {agent: {provider: claude}, result: build, prompt: prompts/build.md}\n  checks: {needs: [build], run: [\"true\"]}\n  review: {needs: [checks], result: review, review: {of: build, agent: {provider: claude}, prompt: prompts/build.md}, on_fail: checks}\n";
        var review = Load(yaml).Definition.Steps["review"];
        Assert.Equal("build", review.RedoTarget); Assert.Equal("checks", review.OnFail);
        Assert.Contains("review.on_fail", Assert.Throws<WorkflowFieldException>(() => Load(yaml.Replace("on_fail: checks", "on_fail: unknown"))).Message);
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
    [Theory]
    [InlineData("limits: {max_rounds: -1}", "limits.max_rounds")]
    [InlineData("limits: {step_timeout_minutes: 9223372036854775807}", "limits.step_timeout_minutes")]
    [InlineData("limits: {max_sessions: 1.5}", "limits.max_sessions")]
    [InlineData("policy: {default: maybe}", "policy.default")]
    [InlineData("inputs: {a: {required: yes}}", "inputs.a.required")]
    [InlineData("inputs: {a: {extra: true}}", "inputs.a.extra")]
    public void Strict_nested_mapping_validation(string section, string field) => Assert.Contains(field,
        Assert.Throws<WorkflowFieldException>(() => Load("name: A\n" + section + "\nsteps: {}\n")).Message);
    [Fact]
    public void Json_compatible_standard_tags_preserve_scalar_types_and_custom_tags_fail()
    {
        var snapshot = Load("name: !!str 123\ninputs: {number: {default: !!int '12'}, flag: {default: !!bool 'true'}, text: {default: !!str true}}\nsteps: {g: {gate: !!map {context: !!seq [!!null null, !!float '1.5']}}}");
        Assert.Equal("123", snapshot.Definition.Name);
        Assert.Equal(12, snapshot.Definition.Inputs["number"].Default!.Value.GetInt32());
        Assert.True(snapshot.Definition.Inputs["flag"].Default!.Value.GetBoolean());
        Assert.Equal("true", snapshot.Definition.Inputs["text"].Default!.Value.GetString());
        Assert.Contains("yaml", Assert.Throws<WorkflowFieldException>(() => Load("name: A\nsteps: {g: {gate: {true: x}}}")).Message);
    }
    [Fact]
    public void Prompt_bom_is_full_snapshot_content_and_invalid_utf8_is_a_field_error()
    {
        var before = Load();
        var path = Path.Combine(root, ".assembly/workflows/prompts/build.md");
        File.WriteAllBytes(path, [0xef, 0xbb, 0xbf, 0x66, 0x69, 0x72, 0x73, 0x74]);
        var withBom = Load();
        Assert.Equal("\ufefffirst", withBom.Prompts["prompts/build.md"]);
        Assert.NotEqual(before.Hash, withBom.Hash);
        File.WriteAllBytes(path, [0xff, 0xfe, 0x61, 0x00]);
        Assert.Contains("steps.build.prompt", Assert.Throws<WorkflowFieldException>(() => Load()).Message);
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
