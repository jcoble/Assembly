using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Workflows;
using Assembly.Engine.Workflows.Receipts;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Engine.Mcp;

public sealed partial class McpGate
{
    internal T ReadReport<T>(McpCaller caller, JsonElement arguments, Func<CallerRow, IReadScope, T> read, CancellationToken ct) =>
        runtime.Read(scope => read(Resolve(scope, caller) ?? ResolveReportReplay(scope, caller, arguments)
            ?? throw new McpRefused(new("session_gone")), scope), ct);

    internal CommandResult ReplayReceipt(McpCaller caller, JsonElement arguments, PreparedWorkflowCommand prepared) => runtime.Write(scope =>
    {
        if (!prepared.ReceiptReplay) throw new McpRefused(new("forbidden"));
        var row = Resolve(scope, caller) ?? ResolveReportReplay(scope, caller, arguments) ?? throw new McpRefused(new("session_gone"));
        // This closed gate performs one original immutable journal replay, never a supplied arbitrary write callback.
        return new McpWrite(row, scope, runtime, limits, time).ApplyWorkflowReceipt(prepared);
    });

    internal CallerRow? ResolveReportReplay(IReadScope scope, McpCaller caller, JsonElement arguments)
    {
        var row = ResolveCore(scope, caller, true);
        if (row?.State != "ended" || arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String || state.GetString() is not ("succeeded" or "failed") ||
            !arguments.TryGetProperty("receipt", out var receipt) || receipt.ValueKind != JsonValueKind.Object) return null;
        if (arguments.EnumerateObject().Any(p => p.Name is not ("state" or "summary" or "proofUrls" or "receipt" or "pr" or "runId"))) return null;
        var hash = WorkflowPreparation.ReceiptHash(receipt, default);
        var matches = scope.Query("""
            SELECT stored.run_id,stored.step_id,stored.attempt,stored.body_json FROM engine_workflow_receipt stored
            JOIN engine_step st ON st.id=stored.step_id AND st.run_id=stored.run_id
              AND st.attempt=stored.attempt AND st.generation=stored.generation
            WHERE stored.session_id=$session AND stored.epoch=$epoch AND stored.machine=$machine AND stored.hash=$hash
              AND st.session_id=$session AND st.authority_session_id=$session AND st.authority_epoch=$epoch
              AND stored.state IN ('pending','accepted','rejected') LIMIT 2
            """, r => (Run: r.Text(0), Step: r.Text(1), Attempt: (int)r.Long(2), Body: r.Text(3)),
            new SqlArg("$session", caller.SessionId), new("$epoch", caller.Epoch), new("$machine", Machine), new("$hash", hash));
        if (matches.Count != 1) return null;
        var match = matches[0];
        try
        {
            var original = JsonSerializer.Deserialize(match.Body, ReceiptPreparationJson.Default.ReceiptBody);
            if (original is null || original.ReportHash != hash || original.Capture.SessionId != caller.SessionId ||
                original.Capture.Epoch != caller.Epoch || original.Capture.StepId != match.Step || original.Report.Attempt != match.Attempt ||
                original.Report.Receipt is null || WorkflowPreparation.ReceiptHash(original.Report.Receipt.Value, default) != hash) return null;
            var prepared = WorkflowPreparation.Receipt(original.Context, original.Report, original.Capture, default, replay: true);
            if (prepared.BodyJson != match.Body || !EngineJournal.HasExactReceipt(scope, original.Context, prepared.CanonicalJournal)) return null;
        }
        catch (JsonException) { return null; }
        return row with { RunId = match.Run, StepId = match.Step, Attempt = match.Attempt };
    }
}
