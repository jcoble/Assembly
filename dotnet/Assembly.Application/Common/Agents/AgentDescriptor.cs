using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Application.Common.Agents;

/// <summary>
/// An agent this machine can run: its id (stored on each thread), the name people see, and how to start its ACP
/// adapter, with the JetBrains AIR extensions to opt into at initialize. Everything else about it comes from what the
/// adapter reports over ACP.
/// </summary>
public sealed record AgentDescriptor(
    string Id, string Name, string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env, IReadOnlyList<string>? Air = null)
{
    /// <summary>The machine's agents, from its agents file: <c>[{ "id", "name", "command", "args": [], "env": {}, "air": [] }]</c>.</summary>
    public static IReadOnlyList<AgentDescriptor> Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), AgentJsonContext.Default.ListAgentDescriptor)
        ?? throw new InvalidOperationException($"{path} lists no agents");

    public AgentCommand ToCommand(string workingDirectory) => new(Command, Args, workingDirectory, Env);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<AgentDescriptor>))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;
