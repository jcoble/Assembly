using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;
using Assembly.Engine.Workflows.Receipts;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    private CommandResult ApplyPreparedReceiptCompletion(IWriteScope scope, CommandContext context, CompleteEffect complete)
    {
        var receipt = complete.Receipt;
        var prepared = receipt.WorkflowReceiptConfirmation!;
        ReceiptConfirmationDescriptor? descriptor;
        try { descriptor = receipt.Data.Deserialize(ReceiptPreparationJson.Default.ReceiptConfirmationDescriptor); }
        catch (JsonException) { descriptor = null; }
        if (receipt.Disposition != EffectDisposition.Confirmed || receipt.Error is not null ||
            descriptor is null || descriptor.Check != prepared.Body.Check || descriptor.MetadataSha256 != prepared.Sha256 ||
            descriptor.BodyBytes != prepared.BodyBytes || context.EntityId != prepared.Body.EffectId)
            return new(false, false, "bad_receipt", context.ExpectedVersion, 0, context.Epoch);
        return EngineJournal.Execute(scope, context, prepared.CanonicalJournal, () =>
            context.Machine != _machine ? new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch)
                : EffectTransitions.Apply(scope, context, complete, _projectCompletion, _expiryPolicy));
    }
}
