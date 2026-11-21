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
