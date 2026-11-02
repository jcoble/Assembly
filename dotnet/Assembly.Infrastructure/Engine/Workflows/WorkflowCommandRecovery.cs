using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Workflows.Execution;

namespace Assembly.Infrastructure.Engine.Workflows;

/// <summary>One explicit recovery request, no process scanner or kill. Absence does not authorize nonrepeatable commands.</summary>
public sealed class WorkflowCommandRecovery(IAppDatabase db, string proofRoot)
{
    public async Task<PreparedWorkflowCommandRetry> PrepareAsync(CommandContext c, CancellationToken ct)
    {
        CommandRetryBody body;
        using (var scope = db.OpenSnapshot(ct))
        {
            var row = scope.QueryFirst("""
                SELECT n.execution_json,o.id,o.version,r.version,owner.version,o.payload_json,o.executor_json
                FROM engine_needs_me n JOIN engine_run r ON r.id=n.run_id JOIN engine_session owner ON owner.id=r.owner_session_id
                JOIN engine_outbox o ON o.id=json_extract(n.execution_json,'$.effectId')
                WHERE n.id=$need AND n.version=$version AND n.state='open' AND n.source_kind='workflow'
                  AND json_extract(n.execution_json,'$.reason')='command_uncertain' AND o.state='unavailable' AND o.kind='workflow_command'
                """, r => new
            {
                Binding = r.Text(0),
                Effect = r.Text(1),
                EffectVersion = r.Long(2),
                RunVersion = r.Long(3),
                OwnerVersion = r.Long(4),
                Payload = r.Text(5),
                Executor = r.IsNull(6) ? null : r.Text(6)
            }, new SqlArg("$need", c.EntityId), new("$version", c.ExpectedVersion))
                ?? throw new InvalidOperationException("command_recovery_unavailable");
            var intent = JsonSerializer.Deserialize(row.Payload, WorkflowExecutionJson.Default.CommandExecutionIntent)!;
            if (!intent.Repeat || row.Executor is null) throw new InvalidOperationException("executor_quiescence_required");
            var process = JsonSerializer.Deserialize(row.Executor, WorkflowExecutionJson.Default.CommandProcessIdentity)!;
            body = new(c, row.Effect, row.EffectVersion, row.RunVersion, row.OwnerVersion, intent, process, CommandStepTransitions.Digest(row.Payload));
        }
        if (!Owned(body.Intent.OutputPath) || body.Process.Pid <= 0 || body.Process.StartUtcTicks <= 0 || body.Process.GroupId != body.Process.Pid ||
            !Guid.TryParseExact(body.Process.Nonce, "N", out _)) throw new InvalidOperationException("executor_quiescence_required");
        var launch = body.Intent.OutputPath + ".launch.json";
        if (!File.Exists(launch) || new FileInfo(launch).Length > 4096 || File.Exists(body.Intent.OutputPath + ".exit.json") ||
            JsonSerializer.Deserialize(await File.ReadAllBytesAsync(launch, ct), WorkflowExecutionJson.Default.CommandProcessIdentity) != body.Process)
            throw new InvalidOperationException("executor_quiescence_required");
        try
        {
            using var process = Process.GetProcessById(body.Process.Pid);
            // Present, reused or unverifiable identity is unknown; none authorizes a retry or a kill.
            throw new InvalidOperationException("executor_quiescence_required");
        }
        catch (ArgumentException) { }
        ct.ThrowIfCancellationRequested();
        if (ProbeGroup(-body.Process.GroupId, 0) != -1 || Marshal.GetLastPInvokeError() != 3)
            throw new InvalidOperationException("executor_quiescence_required");
        return new(body);
    }

    private bool Owned(string path)
    {
        var root = Path.GetFullPath(proofRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(path) || !Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal)) return false;
        for (var parent = Path.GetDirectoryName(path); parent is not null && parent.Length >= root.Length - 1; parent = Path.GetDirectoryName(parent))
            if (new DirectoryInfo(parent).LinkTarget is not null) return false;
        return new FileInfo(path + ".launch.json").LinkTarget is null;
    }
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int ProbeGroup(int pid, int signal);
}
