using Assembly.Application.Common.Engine;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Relays;

namespace Assembly.Tests.Engine.Needs;

public sealed class ParentNeedCodecTests
{
    private static readonly ParentNeedSource Source = new(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
        "parent\0λ", 3, "src😀", "need", ParentNeedAction.Answer);
    private static readonly NeedAnswerTarget Target = new("need", 7, "child", "dst", 11, "local", null,
        "permission", true, "parent\0λ", "src😀", "parent\0λ", "rpc:7", "chat");
    private static readonly ProviderAnswerBinding Binding = new("child", "chat", "rpc:7", new("child", null, "", 2, 11), "acp😀");
    private static NeedAnswerRequestDescriptor Descriptor => new(Source, Target, new(9, new string('0', 64)), Binding);

    [Fact]
    public void Reviewed_64bit_N_Guid_and_appended_binding_fields_match_independent_known_vectors()
    {
        // Independent TypeScript byte-vector calculation uses the exact reviewed field sequence.
        Assert.Equal("parent-need/v1/daf9e7488d82369f02737d48143890cf1cf0dc9959988e876b99eeddcc6f8a9b", ParentNeedCodec.Key(Source, Target));
        Assert.Equal("2eac18ad5afda75c7769314beec68d6f10825ca9590b9e2735b8ef50008af7dc", ParentNeedCodec.Identity(Descriptor));
        Assert.Equal("ae472e9ffbf6ce74bef5b8301617f0f340e9e70a27b25d00148e03b9cfc4eb78", ParentNeedCodec.Identity(Descriptor with { ProviderBinding = null }));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("chat")]
    [InlineData("request")]
    [InlineData("observation_session")]
    [InlineData("run")]
    [InlineData("step")]
    [InlineData("attempt")]
    [InlineData("epoch")]
    [InlineData("acp")]
    public void Every_appended_original_binding_field_changes_descriptor_identity(string field)
    {
        var changed = field switch
        {
            "session" => Binding with { SessionId = "other" },
            "chat" => Binding with { ChatRowId = "other" },
            "request" => Binding with { ProviderRequestId = "other" },
            "observation_session" => Binding with { Observation = Binding.Observation with { SessionId = "other" } },
            "run" => Binding with { Observation = Binding.Observation with { RunId = "" } },
            "step" => Binding with { Observation = Binding.Observation with { StepId = null } },
            "attempt" => Binding with { Observation = Binding.Observation with { Attempt = null } },
            "epoch" => Binding with { Observation = Binding.Observation with { Epoch = 12 } },
            "acp" => Binding with { AcpSessionId = "other" },
            _ => throw new InvalidOperationException()
        };
        Assert.NotEqual(ParentNeedCodec.Identity(Descriptor), ParentNeedCodec.Identity(Descriptor with { ProviderBinding = changed }));
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("action")]
    [InlineData("parent_epoch")]
    [InlineData("child_epoch")]
    [InlineData("version")]
    [InlineData("length")]
    [InlineData("digest")]
    public void Malformed_fixed_descriptor_fields_are_rejected(string field)
    {
        var descriptor = field switch
        {
            "operation" => Descriptor with { Source = Source with { OperationId = Guid.Empty } },
            "action" => Descriptor with { Source = Source with { Action = (ParentNeedAction)7 } },
            "parent_epoch" => Descriptor with { Source = Source with { ParentEpoch = -1 } },
            "child_epoch" => Descriptor with { Target = Target with { ChildEpoch = -1 } },
            "version" => Descriptor with { Target = Target with { NeedVersion = -1 } },
            "length" => Descriptor with { Answer = new(-1, new string('0', 64)) },
            "digest" => Descriptor with { Answer = new(9, new string('G', 64)) },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<RelayRefused>(() => ParentNeedCodec.Identity(descriptor));
    }
}
