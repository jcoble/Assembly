using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mcp;

namespace Assembly.Engine.NeedsMe;

public enum ParentNeedAction { Answer, Escalate }
public sealed record ParentNeedRequest(string NeedId, ParentNeedAction Action, JsonElement? Answer);

public interface INeedAnswers
{
    Task<CommandResult> ApplyAsync(McpCaller caller, ParentNeedRequest request,
        CancellationToken workCancellation, CancellationToken waitCancellation);
}

public sealed record ParentNeedSource(Guid OperationId, string ParentSessionId,
    long ParentEpoch, string SourceMachine, string NeedId, ParentNeedAction Action);
public sealed record NeedAnswerTarget(string NeedId, long NeedVersion,
    string ChildSessionId, string ChildOwnerMachine, long ChildEpoch,
    string ChildHolder, long? ChildReservedEpoch, string Kind, bool ProviderBacked,
    string ParentSessionId, string ParentOwnerMachine, string? AddresseeSessionId,
    string? ProviderRequestId, string? ChatRowId);
public sealed record NeedAnswerManifest(long Utf8Length, string Sha256);
public sealed record NeedAnswerRequestDescriptor(ParentNeedSource Source,
    NeedAnswerTarget Target, NeedAnswerManifest? Answer, ProviderAnswerBinding? ProviderBinding);
public sealed record NeedAnswerAck(string Status, string IdentitySha256,
    CommandResult? Result, string? EffectId, string? EffectState);
