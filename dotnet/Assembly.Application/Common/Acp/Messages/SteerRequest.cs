using System.Text.Json.Serialization;

namespace Assembly.Application.Common.Acp.Messages;

/// <summary>
/// <c>_session/steering</c>: a message for the turn running now. With <c>idleBehavior: promptRequired</c> an idle
/// session answers <c>promptRequired</c> instead of starting a turn of its own.
/// </summary>
public sealed record SteerRequest(string SessionId, IReadOnlyList<TextBlock> Prompt, [property: JsonPropertyName("_meta")] SteerMeta Meta);

public sealed record SteerMeta(SteerOptions Steering);

public sealed record SteerOptions(string IdleBehavior);
