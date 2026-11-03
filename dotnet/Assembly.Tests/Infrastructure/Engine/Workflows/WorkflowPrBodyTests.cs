using System.Collections.Immutable;
using System.Text.Json;
using System.Text;
using Assembly.Engine.Effects;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Execution;
using Assembly.Infrastructure.Engine.Workflows;

namespace Assembly.Tests.Infrastructure.Engine.Workflows;

public sealed class WorkflowPrBodyTests
{
    [Fact]
    public void Derived_body_clips_unicode_scalars_and_keeps_full_source_unchanged()
    {
        var source = string.Concat(Enumerable.Repeat("😀", 7000));
        var body = WorkflowPrBodies.Render(new("https://app.notion.com/p/40a394b0689d81e8a38ef5cf6548be2a", "task", Enumerable.Range(0, 14)
            .Select(i => new WorkflowPrBodySection("step-" + i, source)).ToArray(), new("review", "latest checked review"), []));
        Assert.True(body.EnumerateRunes().Count() <= 60000);
        Assert.Contains("cut; the full output is in the run", body);
        Assert.Contains("latest checked review", body); Assert.Equal(7000, source.EnumerateRunes().Count());
        Assert.DoesNotContain("notion.so/search", body);
        foreach (var i in Enumerable.Range(0, 14)) Assert.Contains("step-" + i + ":", body);
    }

    [Fact]
    public async Task Actual_quoted_body_file_reaches_fake_pr_command_with_latest_checked_text()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("assembly-pr-body-").FullName;
        try
        {
            var proof = Path.Combine(root, "proof with \"quotes\""); Directory.CreateDirectory(proof);
            await File.WriteAllTextAsync(Path.Combine(proof, ".assembly-workflow-allocation"), "run");
            var cli = Path.Combine(root, "fake-pr"); var copy = Path.Combine(root, "copied.txt");
            await File.WriteAllTextAsync(cli, "#!/bin/sh\n[ \"$1\" = --body-file ] || exit 9\ncat \"$2\" > \"$3\"\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var text = WorkflowPrBodies.Render(new("https://app.notion.com/p/40a394b0689d81e8a38ef5cf6548be2a", "", [new("build", "current checked output")],
                new("review", "current checked review"), ["Checked file: capture.png (sha256 fixture, 17 bytes)"]));
            var body = new WorkflowPrBody(Path.Combine(proof, "pr-body.md"), 3, "checked-receipt-set", CommandStepTransitions.Digest(text), text);
            await WorkflowPrBodyFile.WriteAsync(body, proof, "run", default);
            var values = JsonSerializer.SerializeToElement(new { inputs = new { cli, copy }, run = new { pr_body = body.Path } });
            var command = WorkflowTemplates.Commands(["\"${{inputs.cli}}\" --body-file \"${{run.pr_body}}\" \"${{inputs.copy}}\""], values)[0];
            var intent = new CommandExecutionIntent("run", "step", 3, 1, 0, 1, root, command.Script,
                command.Bindings.ToImmutableSortedDictionary(StringComparer.Ordinal), Path.Combine(proof, "output.txt"), false, "tree", 1, body);
            var effect = new Effect("effect", "workflow_command", null, "run", JsonSerializer.SerializeToElement(intent,
                WorkflowExecutionJson.Default.CommandExecutionIntent), 1, intent.OrderKey);
            var process = new WorkflowProcess(proof, (_, _, _, _) => Task.FromResult(true), (_, _) => Task.FromResult("tree"));
            var receipt = await process.ExecuteAsync(effect, false, default);
            Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition);
            Assert.Equal(0, receipt.Data.Deserialize(WorkflowExecutionJson.Default.CommandExecutionReceipt)!.ExitCode);
            Assert.Equal(text, await File.ReadAllTextAsync(copy));
            Assert.Contains("current checked review", await File.ReadAllTextAsync(copy));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Cancelled_atomic_refresh_keeps_previous_complete_file_and_no_temp()
    {
        var root = Directory.CreateTempSubdirectory("assembly-pr-atomic-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".assembly-workflow-allocation"), "run");
            var path = Path.Combine(root, "pr-body.md"); var old = new WorkflowPrBody(path, 0, "old", CommandStepTransitions.Digest("old complete"), "old complete");
            // proofRoot may be the parent of an allocated proof directory, as in the real setup producer.
            await WorkflowPrBodyFile.WriteAsync(old, Path.GetDirectoryName(root)!, "run", default);
            using var stop = new CancellationTokenSource(); await stop.CancelAsync();
            var next = old with { Text = "new complete", Sha256 = CommandStepTransitions.Digest("new complete") };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkflowPrBodyFile.WriteAsync(next, Path.GetDirectoryName(root)!, "run", stop.Token));
            Assert.Equal("old complete", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(root, "*.tmp-*"));
        }
        finally { Directory.Delete(root, true); }
    }
}
