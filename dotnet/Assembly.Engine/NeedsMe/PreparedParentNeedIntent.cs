using System.Text.Json;
using Assembly.Engine.Mcp;
using Assembly.Engine.Relays;

namespace Assembly.Engine.NeedsMe;

/// <summary>Original source request frozen outside the source writer; it confers no admission authority.</summary>
public sealed class PreparedParentNeedIntent
{
    internal ParentNeedIntentPayload Payload { get; }
    internal JsonElement RawPayload { get; }
    internal string SourceKey { get; }
    internal string Canonical { get; }

    private PreparedParentNeedIntent(ParentNeedIntentPayload payload, JsonElement raw)
    {
        Payload = payload; RawPayload = raw;
        SourceKey = ParentNeedCodec.SourceKey(payload.Source);
        Canonical = "{\"kind\":\"parent_need_source\",\"format\":1,\"inputSha256\":\"" + payload.InputSha256 + "\"}";
    }

    internal static PreparedParentNeedIntent Prepare(string machine, Guid operationId, McpCaller caller,
        ParentNeedRequest input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(input.NeedId) || !Enum.IsDefined(input.Action) ||
            input.Action == ParentNeedAction.Escalate && input.Answer is not null ||
            input.Action == ParentNeedAction.Answer && input.Answer is null)
            throw new RelayRefused("bad_args");
        input = input with { Answer = input.Answer?.Clone() };
        if (input.Answer is { } answer) PreparedNeedCommand.ValidateAnswer(answer, ct);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(input, EngineJson.Default.ParentNeedRequest);
        var source = new ParentNeedSource(operationId, caller.SessionId, caller.Epoch, machine, input.NeedId, input.Action);
        var payload = new ParentNeedIntentPayload(1, source, input, RelayHash.Sha(bytes), ParentNeedPhase.Raw, null, null);
        var raw = JsonSerializer.SerializeToElement(payload, EngineJson.Default.ParentNeedIntentPayload);
        ct.ThrowIfCancellationRequested();
        return new(payload, raw);
    }
}
