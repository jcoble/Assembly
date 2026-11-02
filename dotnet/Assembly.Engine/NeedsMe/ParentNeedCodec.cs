using Assembly.Application.Common.Engine;
using Assembly.Engine.Relays;

namespace Assembly.Engine.NeedsMe;

/// <summary>The one closed parent-need key and descriptor codec shared by source, coordinator and target.</summary>
public static class ParentNeedCodec
{
    public static string Key(ParentNeedSource source, NeedAnswerTarget target)
    {
        if (source.OperationId == Guid.Empty) throw new RelayRefused("bad_args");
        using var hash = new RelayHash();
        hash.String("assembly-parent-need-key/v1"); hash.String(source.SourceMachine); hash.String(source.ParentSessionId);
        hash.String(target.ChildOwnerMachine); hash.String(source.NeedId); hash.String(source.OperationId.ToString("N"));
        return "parent-need/v1/" + hash.Finish();
    }

    public static string Identity(NeedAnswerRequestDescriptor descriptor)
    {
        var source = descriptor.Source; var target = descriptor.Target;
        if (source.OperationId == Guid.Empty || !Enum.IsDefined(source.Action)) throw new RelayRefused("bad_args");
        using var hash = new RelayHash();
        hash.String("assembly-parent-need-descriptor/v1"); hash.String(source.OperationId.ToString("N"));
        hash.String(source.SourceMachine); hash.String(source.ParentSessionId); hash.Length(source.ParentEpoch);
        hash.String(source.NeedId); hash.Raw(source.Action == ParentNeedAction.Answer ? [0] : [1]);
        hash.String(target.NeedId); hash.Length(target.NeedVersion); hash.String(target.ChildSessionId);
        hash.String(target.ChildOwnerMachine); hash.Length(target.ChildEpoch); hash.String(target.ChildHolder);
        NullableInteger(hash, target.ChildReservedEpoch); hash.String(target.Kind); hash.Flag(target.ProviderBacked);
        hash.String(target.ParentSessionId); hash.String(target.ParentOwnerMachine); hash.NullableString(target.AddresseeSessionId);
        hash.NullableString(target.ProviderRequestId); hash.NullableString(target.ChatRowId);
        hash.Flag(descriptor.Answer is not null);
        if (descriptor.Answer is { } answer)
        {
            if (!RelayHash.IsSha(answer.Sha256)) throw new RelayRefused("bad_args");
            hash.Length(answer.Utf8Length); hash.Raw(Convert.FromHexString(answer.Sha256));
        }
        Binding(hash, descriptor.ProviderBinding);
        return hash.Finish();
    }

    private static void Binding(RelayHash hash, ProviderAnswerBinding? binding)
    {
        hash.Flag(binding is not null);
        if (binding is null) return;
        if (binding.Observation is not { } observation) throw new RelayRefused("bad_args");
        hash.String(binding.SessionId); hash.String(binding.ChatRowId); hash.String(binding.ProviderRequestId);
        hash.String(observation.SessionId); hash.NullableString(observation.RunId); hash.NullableString(observation.StepId);
        NullableInteger(hash, observation.Attempt); hash.Length(observation.Epoch); hash.String(binding.AcpSessionId);
    }

    private static void NullableInteger(RelayHash hash, long? value)
    {
        hash.Flag(value is not null);
        if (value is { } number) hash.Length(number);
    }
}
