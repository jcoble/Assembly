using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Policy;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Engine.Workflows.Execution;

public sealed class PreparedCommandStep
{
    internal CommandStartBody Body { get; }
    public CommandContext Context { get; }
    public WorkflowPrBody PrBody => Body.Intent.PrBody!;
    public string RunId => Body.Intent.RunId;
    internal string CanonicalJournal { get; }
    internal PreparedCommandStep(CommandStartBody body)
    {
        var identity = JsonSerializer.Serialize(body with { Context = body.Context with { CommandId = Guid.Empty, NowMs = 0 } },
            CommandStartJson.Default.CommandStartBody);
        Context = body.Context with { CommandId = new Guid(Convert.FromHexString(CommandStepTransitions.Digest(identity))[..16]) };
        var json = JsonSerializer.Serialize(body with { Context = Context with { NowMs = 0 } }, CommandStartJson.Default.CommandStartBody);
        Body = JsonSerializer.Deserialize(json, CommandStartJson.Default.CommandStartBody)!;
        CanonicalJournal = JsonSerializer.Serialize(new WorkflowPreparedEnvelope("workflow_command", 1,
            CommandStepTransitions.Digest(json), Encoding.UTF8.GetByteCount(json)), EngineJson.Default.WorkflowPreparedEnvelope);
    }
}
internal sealed record CommandStartBody(CommandContext Context, string DefinitionHash, long RunVersion,
    long StepVersion, CommandExecutionIntent Intent, PolicyDecision Decision, int CommandCount,
    long OwnerVersion, long WorkspaceVersion, IReadOnlyList<CommandDependencyFence> Dependencies);
internal sealed record CommandDependencyFence(string StepId, long Version, int Generation, int Attempt, string State);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CommandStartBody))]
internal sealed partial class CommandStartJson : JsonSerializerContext;

public static class CommandStepPreparation
{
    /// <summary>Call outside the writer after actual checkout capture. The writer rechecks all versions/authority.</summary>
    public static PreparedCommandStep Prepare(IReadScope s, CommandContext c, string fingerprint)
    {
        var row = s.QueryFirst("""
            SELECT st.version,st.run_id,st.definition_key,st.generation,st.attempt,st.state,
              r.version,r.definition_hash,r.proof_dir,w.path,COALESCE(a.command_index,0),
              COALESCE(a.execution_epoch,0),a.fingerprint,a.gate_state,snap.snapshot_json,owner.version,w.version
            FROM engine_step st JOIN engine_run r ON r.id=st.run_id
            JOIN engine_workflow_snapshot snap ON snap.run_id=r.id
            JOIN engine_session owner ON owner.id=r.owner_session_id
            JOIN engine_workflow_workspace w ON w.id=st.workspace_id AND w.state='ready'
            LEFT JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=st.attempt
            WHERE st.id=$step AND st.kind='command' AND r.setup_state='ready'
            """, r => new
        {
            Version = r.Long(0),
            Run = r.Text(1),
            Key = r.Text(2),
            Generation = checked((int)r.Long(3)),
            Attempt = checked((int)r.Long(4)),
            State = r.Text(5),
            RunVersion = r.Long(6),
            Hash = r.Text(7),
            Proof = r.Text(8),
            Cwd = r.Text(9),
            Index = checked((int)r.Long(10)),
            Epoch = r.Long(11),
            Fingerprint = r.IsNull(12) ? null : r.Text(12),
            Gate = r.IsNull(13) ? null : r.Text(13),
            Snapshot = r.Text(14),
            OwnerVersion = r.Long(15),
            WorkspaceVersion = r.Long(16)
        }, new SqlArg("$step", c.EntityId))
            ?? throw new InvalidOperationException("command_workspace_not_ready");
        var first = row.State == "pending";
        if (!first && (row.State != "running" || row.Gate != "ready")) throw new InvalidOperationException("command_not_ready");
        var start = JsonSerializer.Deserialize(row.Snapshot, WorkflowPreparationJson.Default.WorkflowStartBody)!;
        var spec = start.Snapshot.Definition.Steps[row.Key];
        var bindings = EngineCommands.TemplateValues(s, start, row.Run);
        var index = first ? 0 : row.Index;
        if (index >= spec.Commands.Length) throw new InvalidOperationException("command_already_settled");
        var command = WorkflowTemplates.Commands([spec.Commands[index]], bindings)[0];
        var decision = start.Snapshot.Definition.Policy.Decide(WorkflowTemplates.Render(spec.Commands[index], bindings));
        var attempt = first ? checked(row.Attempt + 1) : row.Attempt;
        var intent = new CommandExecutionIntent(row.Run, c.EntityId, row.Generation, attempt, index,
            first ? checked(row.Epoch + 1) : row.Epoch, row.Cwd, command.Script,
            command.Bindings.ToImmutableSortedDictionary(StringComparer.Ordinal),
            Path.Combine(row.Proof, $"{c.EntityId}-{row.Generation}-{attempt}-{index}.txt"), spec.Repeat,
            first ? fingerprint : row.Fingerprint ?? throw new InvalidOperationException("command_fingerprint_missing"), spec.Commands.Length,
            WorkflowPrBodies.Prepare(s, row.Run, c.EntityId, row.Generation));
        return new(new(c, row.Hash, row.RunVersion, row.Version, intent, decision, spec.Commands.Length,
            row.OwnerVersion, row.WorkspaceVersion, Dependencies(s, c.EntityId)));
    }

    internal static IReadOnlyList<CommandDependencyFence> Dependencies(IReadScope s, string step) => s.Query("""
        SELECT dep.id,dep.version,dep.generation,dep.attempt,dep.state FROM engine_step_dependency d
        JOIN engine_step dep ON dep.id=d.dependency_id WHERE d.step_id=$step ORDER BY dep.id
        """, r => new CommandDependencyFence(r.Text(0), r.Long(1), checked((int)r.Long(2)), checked((int)r.Long(3)), r.Text(4)), new SqlArg("$step", step));

}
