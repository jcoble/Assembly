using System.Text.Json.Serialization;
using Assembly.Engine.Contracts;

namespace Assembly.Engine.NeedsMe;

public enum ParentNeedPhase { Raw, Prepared, Executing, Outcome }

public sealed record ParentNeedPrepared(NeedAnswerRequestDescriptor Descriptor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte[]? AnswerUtf8 = null);

public sealed record ParentNeedIntentPayload(int Format, ParentNeedSource Source,
    ParentNeedRequest Input, string InputSha256, ParentNeedPhase Phase,
    ParentNeedPrepared? Prepared, ParentNeedOutcome? Outcome);

public sealed record ParentNeedOutcome(string Origin, string Status,
    string IdentitySha256, CommandResult? Result, string? EffectId,
    string? EffectState, string? Error);

public sealed record ParentNeedIntentAccepted(string IntentId, CommandResult Result);

public sealed record ParentNeedCursor(long CreatedMs, string IntentId);

public sealed record ParentNeedPendingArgs(ParentNeedCursor? After = null,
    ParentNeedCursor? Cut = null, int Limit = 100);

public sealed record ParentNeedPendingItem(string IntentId, long Version,
    ParentNeedSource Source, ParentNeedPhase Phase, string InputSha256,
    string? PreparedDescriptorH, long CreatedMs);

public sealed record ParentNeedPendingPage(IReadOnlyList<ParentNeedPendingItem> Items,
    ParentNeedCursor? Next, ParentNeedCursor? Cut, bool More);
