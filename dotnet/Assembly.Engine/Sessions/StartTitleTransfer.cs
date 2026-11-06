using Assembly.Application.Common.Projects;
using Assembly.Application.Common.Engine;
using System.Text.Json.Serialization;

namespace Assembly.Engine.Sessions;

public enum StartTitlePartKind { Facts, Title }
public sealed record StartTitleBeginArgs(int StageVersion, string TargetMachine, MachineStartAckArgs Start,
    ForeignTitlePartManifest Facts, ForeignTitlePartManifest Title);
public sealed record StartTitleBeginReply(MachineStartAck Evidence, string? Handle);
public sealed record StartTitlePartArgs(string Handle, StartTitlePartKind Part, long Offset, byte[] Data);
public sealed record StartTitleOffsets(long FactsNext, long TitleNext);
public sealed record StartTitleCommitArgs(StartTitleBeginArgs Request, string? Handle = null);
internal sealed record StartTitleFacts(string Provider, string? Model, string Folder, string? ParentId, string? RunId, string? TaskUrl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RepoUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ManagedCheckout = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProjectId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CheckoutIntent? Checkout = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(StartTitleFacts))]
internal partial class StartTitleJson : JsonSerializerContext;
