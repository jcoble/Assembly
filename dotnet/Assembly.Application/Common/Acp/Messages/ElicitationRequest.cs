using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Application.Common.Acp.Messages;

/// <summary>
/// <c>elicitation/create</c> in form mode: the agent asks the user to fill <c>RequestedSchema</c>, a flat JSON Schema
/// object (strings, <c>oneOf</c> choices, arrays of <c>anyOf</c> choices). Claude Code's AskUserQuestion comes this way.
/// </summary>
public sealed record ElicitationRequest(string SessionId, string Mode, string? Message, JsonElement RequestedSchema, string? ToolCallId);

/// <summary><c>{ action: "accept", content }</c>, <c>{ action: "decline" }</c> (skip) or <c>{ action: "cancel" }</c>.</summary>
public sealed record ElicitationResponse(string Action, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Content = default)
{
    public static ElicitationResponse Cancel { get; } = new("cancel");
}

/// <summary>We fill forms; we don't open URLs for the agent.</summary>
public sealed record ElicitationCapabilities(FormElicitation Form);

public sealed record FormElicitation;
