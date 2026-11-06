using System.Text.Json.Serialization;
using Assembly.Engine.Workflows.Receipts;

namespace Assembly.Engine.Effects;

public sealed partial record EffectReceipt
{
    [JsonIgnore]
    internal PreparedReceiptConfirmation? WorkflowReceiptConfirmation { get; init; }
}
