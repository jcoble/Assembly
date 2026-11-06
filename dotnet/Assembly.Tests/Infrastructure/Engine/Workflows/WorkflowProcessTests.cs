using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Assembly.Engine.Effects;
using Assembly.Engine.Workflows.Execution;
using Assembly.Infrastructure.Engine.Workflows;

namespace Assembly.Tests.Infrastructure.Engine.Workflows;

public sealed class WorkflowProcessTests
{
    [Fact]
    public async Task Nonzero_exit_is_observed_and_reopen_reuses_receipt_without_reexecution()
    {
        using var f = new Fixture("printf 'first\\nsecond\\n'; exit 7");
        var starts = 0;
        var host = f.Host((_, _, _, _) => { starts++; return Task.FromResult(true); });
        var first = await host.ExecuteAsync(f.Effect, false, default);
        Assert.Equal(EffectDisposition.Confirmed, first.Disposition);
        var result = first.Data.Deserialize(WorkflowExecutionJson.Default.CommandExecutionReceipt)!;
        Assert.Equal(7, result.ExitCode); Assert.Contains("second", result.Summary); Assert.False(result.Stopped);
        var replay = await f.Host().ExecuteAsync(f.Effect, true, default);
        Assert.Equal(first.Data.GetRawText(), replay.Data.GetRawText()); Assert.Equal(1, starts);
        Assert.Equal(result.OutputSha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(result.OutputReceiptPath))));
    }

    [Fact]
    public async Task Cancel_waits_for_the_exact_started_process_and_pipe_drain()
    {
        using var f = new Fixture("while :; do printf 'working\\n'; done");
        var observed = new TaskCompletionSource<CommandProcessIdentity>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var running = f.Host((_, _, process, _) => { observed.SetResult(process); return Task.FromResult(true); }).ExecuteAsync(f.Effect, false, stop.Token);
        var identity = await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        var receipt = await running.WaitAsync(TimeSpan.FromSeconds(10));
        var result = receipt.Data.Deserialize(WorkflowExecutionJson.Default.CommandExecutionReceipt)!;
        Assert.True(result.Stopped); Assert.Equal(identity, result.Process); Assert.NotNull(result.ExitCode);
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(identity.Pid));
    }

    [Fact]
    public async Task Uncertain_restart_never_launches_and_corrupt_output_never_reconciles_success()
    {
        using var f = new Fixture("printf once");
        var starts = 0;
        var uncertain = await f.Host((_, _, _, _) => { starts++; return Task.FromResult(true); }).ExecuteAsync(f.Effect, true, default);
        Assert.Equal(EffectDisposition.Unknown, uncertain.Disposition); Assert.Equal(0, starts);
        var done = await f.Host().ExecuteAsync(f.Effect, false, default);
        var result = done.Data.Deserialize(WorkflowExecutionJson.Default.CommandExecutionReceipt)!;
        await File.WriteAllTextAsync(result.OutputReceiptPath, "changed");
        var corrupt = await f.Host().ExecuteAsync(f.Effect, true, default);
        Assert.Equal(EffectDisposition.Unknown, corrupt.Disposition); Assert.Equal("command_output_changed", corrupt.Error);
    }

    [Fact]
    public async Task Output_cap_retains_digest_descriptor_and_counts_all_observed_bytes()
    {
        using var f = new Fixture("head -c 16777316 /dev/zero");
        var done = await f.Host().ExecuteAsync(f.Effect, false, default);
        var result = done.Data.Deserialize(WorkflowExecutionJson.Default.CommandExecutionReceipt)!;
        Assert.True(result.Truncated); Assert.Equal(16 * 1024 * 1024, result.OutputBytes); Assert.Equal(16777316, result.ObservedBytes);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.Summary) <= 8192);
        Assert.True(done.Data.GetRawText().Length < 32768);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "assembly-workflow-process-" + Guid.NewGuid().ToString("N"));
        public Effect Effect { get; }
        public Fixture(string script)
        {
            Directory.CreateDirectory(_root);
            var proof = Path.Combine(_root, "proof"); Directory.CreateDirectory(proof);
            File.WriteAllText(Path.Combine(proof, ".assembly-workflow-allocation"), "run");
            var text = "Request: actual command fixture";
            var body = new WorkflowPrBody(Path.Combine(proof, "pr-body.md"), 0, "inputset", CommandStepTransitions.Digest(text), text);
            WorkflowPrBodyFile.WriteAsync(body, proof, "run", default).GetAwaiter().GetResult();
            var intent = new CommandExecutionIntent("run", "step", 0, 1, 0, 1, _root, script,
                ImmutableSortedDictionary<string, string>.Empty, Path.Combine(_root, "proof", "output.txt"), false, "tree", 1, body);
            Effect = new("effect", "workflow_command", null, "run", JsonSerializer.SerializeToElement(intent,
                WorkflowExecutionJson.Default.CommandExecutionIntent), 1, intent.OrderKey);
        }
        public WorkflowProcess Host(Func<Effect, CommandExecutionIntent, CommandProcessIdentity, CancellationToken, Task<bool>>? observed = null) =>
            new(Path.Combine(_root, "proof"), observed ?? ((_, _, _, _) => Task.FromResult(true)), (_, _) => Task.FromResult("tree"));
        public void Dispose() => Directory.Delete(_root, true);
    }
}
