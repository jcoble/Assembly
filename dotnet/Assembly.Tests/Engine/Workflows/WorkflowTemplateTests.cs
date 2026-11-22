using System.Diagnostics;
using System.Text.Json;
using Assembly.Engine.Workflows.Definitions;

namespace Assembly.Tests.Engine.Workflows;

public sealed class WorkflowTemplateTests
{
    static JsonElement Values(string json) => JsonDocument.Parse(json).RootElement.Clone();
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
