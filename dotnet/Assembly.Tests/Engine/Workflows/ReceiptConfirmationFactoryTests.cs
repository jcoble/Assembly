using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Effects;
using Assembly.Engine.Workflows.Receipts;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Tests.Engine.Workflows;

public sealed class ReceiptConfirmationFactoryTests
{
    [Fact]
    public void Large_checked_metadata_is_frozen_while_public_effect_receipt_stays_a_descriptor()
    {
        var files = Enumerable.Range(0, 800).Select(i => new ReceiptMatchedFile("proof/" + i + ".png", "proof", new string('c', 64), i)).ToArray();
        var receipt = Prepare(files);
        var prepared = receipt.WorkflowReceiptConfirmation!;
        Assert.True(prepared.BodyBytes > 32768); Assert.Equal(800, prepared.Body.Files.Count);
        var first = prepared.Body.Files[0]; files[0] = first with { Path = "replaced.png" };
        Assert.Equal(first, prepared.Body.Files[0]);
        var encoded = JsonSerializer.Serialize(receipt, EngineJson.Default.EffectReceipt);
        Assert.True(encoded.Length < 4096);
        var decoded = JsonSerializer.Deserialize(encoded, EngineJson.Default.EffectReceipt)!;
        Assert.Null(decoded.WorkflowReceiptConfirmation);
        Assert.Equal(receipt.Data.GetRawText(), decoded.Data.GetRawText());
    }

    [Fact]
    public void Same_size_last_metadata_change_changes_the_prepared_journal_fingerprint()
    {
        var first = Prepare([new("a.png", "proof", new string('c', 64), 4)]).WorkflowReceiptConfirmation!;
        var second = Prepare([new("b.png", "proof", new string('c', 64), 4)]).WorkflowReceiptConfirmation!;
        Assert.Equal(first.BodyBytes, second.BodyBytes);
        Assert.NotEqual(first.Sha256, second.Sha256);
        Assert.NotEqual(first.CanonicalJournal, second.CanonicalJournal);
    }

    private static EffectReceipt Prepare(IReadOnlyList<ReceiptMatchedFile> files)
    {
        var intent = new ReceiptVerificationIntent("receipt", "step", 1, 0, new string('a', 64));
        var effect = new Effect("effect", "workflow_receipt", null, "run",
            JsonSerializer.SerializeToElement(intent, ReceiptPreparationJson.Default.ReceiptVerificationIntent), 1, "worker");
        return WorkflowPreparation.ReceiptConfirmation(effect, new("receipt", 1, 0, false, new string('b', 40), "proof_file_missing"),
            new string('d', 64), files, [], default);
    }
}
