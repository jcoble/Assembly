using System.Text.Json;

namespace Assembly.Domain.Threads;

/// <summary>
/// A thread's session config: the options its agent reported (raw JSON, null before its first session), the values
/// chosen, and the agent's slash commands (ACP <c>availableCommands</c>, raw JSON, null before it sent any).
/// </summary>
public sealed record ThreadConfig(string? Options, IReadOnlyDictionary<string, string> Choices, string? Commands = null)
{
    public static IReadOnlyDictionary<string, string> ReadChoices(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .Where(choice => choice.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(choice => choice.Name, choice => choice.Value.GetString()!);
    }
}
