using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Workflows;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    private CommandResult ReplayPreparedReceipt(IWriteScope scope, CommandContext context, PreparedWorkflowCommand prepared)
    {
        var body = prepared.Receipt!;
        if (context.Machine != _machine || body.Context != context with { NowMs = 0 })
            return new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch);
        // Never reconstruct a missing journal result from a surviving private body.
        if (scope.QueryFirst("""
            SELECT COUNT(*) FROM engine_workflow_receipt receipt JOIN engine_command_receipt root ON root.command_id=$command
            WHERE receipt.id=$receipt AND receipt.hash=$hash AND receipt.body_json=$body
            """, r => r.Long(0), new SqlArg("$command", context.CommandId.ToString("N")),
            new("$receipt", body.ReceiptId), new("$hash", body.ReportHash), new("$body", prepared.BodyJson)) != 1)
            return new(false, false, "receipt_unknown", context.ExpectedVersion, 0, context.Epoch);
        return EngineJournal.Execute(scope, context, prepared.CanonicalJournal,
            () => throw new InvalidOperationException("Receipt replay transition must be unreachable."));
    }
}
