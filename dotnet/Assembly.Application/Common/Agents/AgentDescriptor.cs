using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Application.Common.Agents;

/// <summary>
/// An agent this machine can run: its id (stored on each session), the name people see, and how to start its ACP
/// adapter, with the JetBrains AIR extensions to opt into at initialize, and the full path of its CLI when the agents file
/// names one (a backend started from the Dock has a short PATH). Everything else about it comes from what the
/// adapter reports over ACP.
/// </summary>
public sealed record AgentDescriptor(
    string Id, string Name, string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env, IReadOnlyList<string>? Air = null,
    string? Cli = null)
{
    /// <summary>The machine's agents, from its agents file's text: <c>[{ "id", "name", "command", "args": [], "env": {}, "air": [], "cli" }]</c>.</summary>
    public static IReadOnlyList<AgentDescriptor> Parse(string json) =>
        JsonSerializer.Deserialize(json, AgentJsonContext.Default.ListAgentDescriptor)
        ?? throw new InvalidOperationException("The agents file lists no agents");

    /// <summary>The CLI to run for agent <paramref name="id"/>: its full path from the agents file, else its plain name.</summary>
    public static string CliOf(IReadOnlyList<AgentDescriptor> agents, string id) =>
        agents.FirstOrDefault(agent => agent.Id == id)?.Cli ?? id;

    public AgentCommand ToCommand(string workingDirectory) => new(Command, Args, workingDirectory, Env);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<AgentDescriptor>))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;
