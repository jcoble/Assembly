using System.Text.Json;

namespace Assembly.Domain.Sessions;

/// <summary>
/// A session's config: the options its agent reported (raw JSON, null before its first session), the values
/// chosen, and the agent's slash commands (ACP <c>availableCommands</c>, raw JSON, null before it sent any).
/// </summary>
public sealed record SessionConfig(string? Options, IReadOnlyDictionary<string, string> Choices, string? Commands = null)
{
    public static IReadOnlyDictionary<string, string> ReadChoices(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .Where(choice => choice.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(choice => choice.Name, choice => choice.Value.GetString()!);
    }
}
