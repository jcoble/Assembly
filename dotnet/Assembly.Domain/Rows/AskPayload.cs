using System.Text.Json;

namespace Assembly.Domain.Rows;

/// <summary>
/// A card asking the user. <c>Kind</c> is <c>permission</c> (pick one of the agent's <c>Options</c>, by their ACP
/// kinds <c>allow_once</c>, <c>allow_always</c>, <c>reject_once</c>, <c>reject_always</c>) or <c>question</c> (fill
/// <c>Schema</c>, a flat JSON Schema form). <c>State</c> is <c>open</c>, <c>answered</c> or <c>cancelled</c>;
/// <c>Answer</c> is the chosen option's id, or the form's <c>{ action, content }</c>.
/// </summary>
public sealed record AskPayload(
    string Kind, string State, string? Title, string? Message, string? ToolCallId,
    IReadOnlyList<AskOption>? Options, JsonElement? Schema, JsonElement? Answer);

public sealed record AskOption(string OptionId, string Name, string Kind);
