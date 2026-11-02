using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Engine.Mcp;
using Assembly.Engine.Relays;

namespace Assembly.Engine.NeedsMe;

/// <summary>Frozen input for the fixed addressed-parent transition; this object confers no authority.</summary>
public sealed class PreparedParentNeed
{
    internal Guid OperationId { get; }
    internal ParentNeedRequest Request { get; }
    internal NeedAnswerTarget Target { get; }
    internal ProviderAnswerBinding? ProviderBinding { get; }
    internal byte[]? AnswerUtf8 { get; }
    internal NeedAnswerManifest? Manifest { get; }
    internal JsonElement? AnswerPayload { get; }
    internal PreparedNeedCommand.ResolutionNote? Note { get; }

    private PreparedParentNeed(Guid operationId, ParentNeedRequest request, NeedAnswerTarget target,
        ProviderAnswerBinding? binding, byte[]? answerUtf8, NeedAnswerManifest? manifest, JsonElement? payload,
        PreparedNeedCommand.ResolutionNote? note) =>
        (OperationId, Request, Target, ProviderBinding, AnswerUtf8, Manifest, AnswerPayload, Note) =
        (operationId, request, target, binding, answerUtf8, manifest, payload, note);

    internal static PreparedParentNeed Prepare(Guid operationId, ParentNeedRequest request, NeedAnswerTarget target,
        ProviderAnswerBinding? binding, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        request = request with { Answer = request.Answer?.Clone() };
        byte[]? bytes = null; NeedAnswerManifest? manifest = null; JsonElement? payload = null;
        PreparedNeedCommand.ResolutionNote? note = null;
        if (request.Answer is { } answer)
        {
            PreparedNeedCommand.ValidateAnswer(answer, ct);
            bytes = JsonSerializer.SerializeToUtf8Bytes(answer, EngineJson.Default.JsonElement);
            manifest = new(bytes.LongLength, RelayHash.Sha(bytes));
            if (target.ProviderBacked && binding is not null)
                payload = JsonSerializer.SerializeToElement(new NeedAnswer(request.NeedId, binding.ProviderRequestId, answer, Binding: binding), EngineJson.Default.NeedAnswer);
            else if (!target.ProviderBacked)
                note = PreparedNeedCommand.PrepareResolutionNote(request.NeedId, new ResolveNeed("answered", answer));
        }
        ct.ThrowIfCancellationRequested();
        return new(operationId, request, target, binding, bytes, manifest, payload, note);
    }

    internal string LocalCanonical(McpCaller caller, string machine)
    {
        var source = new ParentNeedSource(OperationId, caller.SessionId, caller.Epoch, machine, Request.NeedId, Request.Action);
        var descriptor = new NeedAnswerRequestDescriptor(source, Target, Manifest, ProviderBinding);
        return "{\"kind\":\"parent_need\",\"parentNeedJournalVersion\":1,\"identitySha256\":\"" + ParentNeedCodec.Identity(descriptor) + "\"}";
    }
}
