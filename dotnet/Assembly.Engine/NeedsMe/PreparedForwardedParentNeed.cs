using Assembly.Engine.Relays;

namespace Assembly.Engine.NeedsMe;

/// <summary>The fixed forwarded parent operation; only its target writer grants authority.</summary>
internal enum ParentNeedPreparationError { None, BadAnswer, RequestGone }

public sealed class PreparedForwardedParentNeed
{
    internal ParentNeedSource Source { get; }
    internal NeedAnswerTarget Target { get; }
    internal PreparedParentNeed Prepared { get; }
    internal NeedAnswerRequestDescriptor Descriptor { get; }
    internal string Canonical { get; }
    internal ParentNeedPreparationError PreparationError { get; }

    private PreparedForwardedParentNeed(ParentNeedSource source, NeedAnswerTarget target, PreparedParentNeed prepared,
        ParentNeedPreparationError error)
    {
        Source = source; Target = target; Prepared = prepared; PreparationError = error;
        Descriptor = new(source, target, prepared.Manifest, prepared.ProviderBinding);
        Canonical = "{\"kind\":\"parent_need\",\"parentNeedJournalVersion\":1,\"identitySha256\":\"" + ParentNeedCodec.Identity(Descriptor) + "\"}";
    }

    internal static PreparedForwardedParentNeed Prepare(ParentNeedSource source, NeedAnswerTarget target, PreparedParentNeed prepared,
        ParentNeedPreparationError error = ParentNeedPreparationError.None)
    {
        if (source.OperationId != prepared.OperationId || source.NeedId != prepared.Request.NeedId ||
            source.Action != prepared.Request.Action || target != prepared.Target || target.NeedId != source.NeedId ||
            target.ParentSessionId != source.ParentSessionId || target.ParentOwnerMachine != source.SourceMachine ||
            target.AddresseeSessionId != source.ParentSessionId || target.ChildOwnerMachine == source.SourceMachine)
            throw new RelayRefused("bad_args");
        return new(source, target, prepared, error);
    }
}
