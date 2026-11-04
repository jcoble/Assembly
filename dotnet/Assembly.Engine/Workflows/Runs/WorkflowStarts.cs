using System.Collections.Immutable;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Projects;
using Assembly.Engine.Contracts;
using Assembly.Engine.Workflows.Definitions;

namespace Assembly.Engine.Workflows.Runs;

public static partial class WorkflowStarts
{
    public static WorkflowStartAck? Existing(IReadScope scope, WorkflowStartRequest request, ProjectRecord project)
    {
        var found = Read(scope, request);
        if (found is null) return null;
        var result = JsonSerializer.Deserialize(found.Result, EngineJson.Default.CommandResult)!;
        if (found.Fingerprint != WorkflowPreparation.RequestFingerprint(request, project.Id, project.RootPath))
            result = new(false, false, "request_key_conflict", result.Version, 0, result.Epoch);
        return new(found.RunId, found.DefinitionHash, result);
    }

    // The caller uses this closed SQL preparation BEFORE deciding whether to load any files.
    public static PreparedWorkflowCommand? PrepareExisting(IReadScope scope, CommandContext context,
        WorkflowStartRequest request, ProjectRecord project, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var found = Read(scope, request);
        if (found is null) return null;
        var original = JsonSerializer.Deserialize(found.Body, WorkflowPreparationJson.Default.WorkflowStartBody)!;
        return WorkflowPreparation.Freeze(original with
        {
            Context = context with { NowMs = 0 },
            Request = Freeze(request),
            RequestFingerprint = WorkflowPreparation.RequestFingerprint(request, project.Id, project.RootPath),
            ProjectId = project.Id,
            ProjectRoot = project.RootPath
        }, ct);
    }

    // File/path containment belongs to the existing Infrastructure WorkflowLoader. This is pure preparation.
    public static PreparedWorkflowCommand Prepare(CommandContext context, WorkflowStartRequest request,
        WorkflowSnapshot snapshot, ProjectRecord project, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var validated = WorkflowParser.Parse(new(snapshot.Yaml, snapshot.Prompts), ct);
        var frozen = Freeze(request);
        var resolved = Resolved(request, project.RootPath);
        var inputs = WorkflowParser.ResolveInputs(validated.Definition, frozen.Inputs, resolved);
        var ids = validated.Definition.Steps.Keys.ToImmutableSortedDictionary(key => key, _ => Guid.NewGuid().ToString("N"), StringComparer.Ordinal);
        return WorkflowPreparation.Freeze(new WorkflowStartBody(context with { NowMs = 0 }, frozen, project.Id, project.RootPath,
            validated, inputs, ids, WorkflowPreparation.RequestFingerprint(frozen, project.Id, project.RootPath)), ct);
    }

    public static WorkflowStartAck Apply(IWriteScope scope, EngineCommands commands, CommandContext context, PreparedWorkflowCommand prepared)
    {
        var result = commands.ApplyPreparedWorkflow(scope, context, prepared);
        var found = Read(scope, prepared.Start.Request);
        return found is null ? new("", "", result) : new(found.RunId, found.DefinitionHash, result);
    }

    internal static StartMapping? Read(IReadScope scope, WorkflowStartRequest request) => scope.QueryFirst("""
        SELECT st.run_id,st.request_fingerprint,s.definition_hash,s.snapshot_json,c.result_json
        FROM engine_workflow_start st JOIN engine_workflow_snapshot s ON s.run_id=st.run_id
        JOIN engine_command_receipt c ON c.command_id=st.original_command_id
        WHERE st.owner_id=$owner AND st.request_key=$key
        """, r => new StartMapping(r.Text(0), r.Text(1), r.Text(2), r.Text(3), r.Text(4)),
        new SqlArg("$owner", request.OwnerSessionId), new("$key", request.RequestKey));

    internal sealed record StartMapping(string RunId, string Fingerprint, string DefinitionHash, string Body, string Result);

    private static WorkflowStartRequest Freeze(WorkflowStartRequest request) => request with
    { Inputs = request.Inputs.Clone(), Task = request.Task?.Clone() };

    private static JsonElement Resolved(WorkflowStartRequest request, string root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("request", request.Request); writer.WriteString("cwd", root);
            writer.WritePropertyName("task");
            if (request.Task is { } task) task.WriteTo(writer); else writer.WriteNullValue();
            writer.WriteEndObject();
        }
        using var parsed = JsonDocument.Parse(stream.ToArray()); return parsed.RootElement.Clone();
    }
}
