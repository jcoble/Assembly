using System.Diagnostics;
using System.Text.Json;
using Assembly.Engine.Workflows.Definitions;

namespace Assembly.Tests.Engine.Workflows;

public sealed class WorkflowTemplateTests
{
    static JsonElement Values(string json) => JsonDocument.Parse(json).RootElement.Clone();
    [Fact]
    public void Literal_values_and_first_attempt_redo_preserve_content()
    {
        var values = Values("""{"inputs":{"request":"fix $() `x`; quotes \"yes\"","flag":true},"steps":{"build":{"files":["a","b"],"summary":"done"}},"run":{"pr_body":"/proof/file"}}""");
        Assert.Equal("fix $() `x`; quotes \"yes\"//false/[\"a\",\"b\"]", WorkflowTemplates.Render("${{ inputs.request }}/${{ redo.output }}/${{ !inputs.flag }}/${{ steps.build.files }}", values));
        Assert.False(WorkflowTemplates.Condition("${{ !inputs.flag }}", values));
        Assert.Throws<WorkflowFieldException>(() => WorkflowTemplates.Render("${{ task.key }}", values));
        Assert.Throws<WorkflowFieldException>(() => WorkflowTemplates.Render("${{ inputs.request.extra }}", values));
        Assert.Throws<WorkflowFieldException>(() => WorkflowTemplates.Render("${{ !inputs.request }}", values));
        Assert.Throws<WorkflowFieldException>(() => WorkflowTemplates.Condition("${{ inputs.request }}", values));
        Assert.Throws<WorkflowFieldException>(() => WorkflowTemplates.Condition("\"true\"", values));
        Assert.Throws<WorkflowFieldException>(() => WorkflowTemplates.Condition("${{ inputs.flag }}", Values("{\"inputs\":{\"flag\":\"true\"}}")));
        Assert.Throws<WorkflowFieldException>(() => WorkflowTemplates.Render("${{ inputs.flag", values));
    }
    [Fact]
    public void Structured_values_use_compact_json_like_the_old_renderer()
    {
        Assert.Equal("{\"text\":\"<é>\",\"n\":1}", WorkflowTemplates.Render("${{ inputs.object }}", Values("{\"inputs\":{\"object\":{ \"text\" : \"<é>\", \"n\" : 1 }}}")));
    }
    [Fact]
    public async Task Shell_bindings_preserve_quoted_unquoted_and_single_quoted_old_semantics()
    {
        var path = Path.Combine(Path.GetTempPath(), "assembly-bindings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            File.WriteAllText(Path.Combine(path, "glob-one.txt"), "");
            var values = Values("""{"inputs":{"quoted":"a path/with \"quotes\" ; $(touch injected) `touch injected`","word":"one; $(touch injected) `touch injected` glob-*.txt"}}""");
            var prepared = WorkflowTemplates.Commands(["printf '<%s>\\n' \"${{ inputs.quoted }}\" ${{ inputs.word }} '${{ inputs.quoted }}'"], values).Single();
            Assert.DoesNotContain("touch injected", prepared.Script);
            var start = new ProcessStartInfo("/bin/sh") { WorkingDirectory = path, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add(prepared.Script);
            foreach (var binding in prepared.Bindings) start.Environment[binding.Key] = binding.Value;
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("<a path/with \"quotes\" ; $(touch injected) `touch injected`>\n<one;>\n<$(touch>\n<injected)>\n<`touch>\n<injected`>\n<glob-one.txt>\n<${ASSEMBLY_V2}>\n", output);
            Assert.False(File.Exists(Path.Combine(path, "injected")));
        }
        finally { Directory.Delete(path, true); }
    }
}
