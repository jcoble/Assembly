using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Application.Common.Agents;

/// <summary>
/// An agent this machine can run: its id (stored on each session), the name people see, how to start its ACP adapter,
/// and the full path of its CLI when the agents file names one (a backend started from the Dock has a short PATH).
/// Everything else about it comes from what the adapter reports over ACP.
/// </summary>
public sealed record AgentDescriptor(
    string Id, string Name, string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env,
    string? Cli = null, string? VendorFamily = null,
    // Legacy configuration identity input only. Never emitted in ACP V2 initialization.
    IReadOnlyList<string>? Air = null,
    IReadOnlyList<string>? CliArgs = null, string? ShadowHome = null)
{
    private readonly IReadOnlyDictionary<string, string>? _declaredEnv = CopyEnvironment(Env);

    /// <summary>The explicit configuration before a captured launch replaces Env with its effective environment.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string>? DeclaredEnv
    {
        get => _declaredEnv;
        init => _declaredEnv = CopyEnvironment(value);
    }

    private static IReadOnlyDictionary<string, string>? CopyEnvironment(IReadOnlyDictionary<string, string>? environment) =>
        environment is null ? null : new ReadOnlyDictionary<string, string>(
            environment.ToDictionary(pair => pair.Key, pair => pair.Value, ConfigTargetIdentity.EnvironmentComparer));

    /// <summary>The machine's agents, from its agents file's text: <c>[{ "id", "name", "command", "args": [], "env": {}, "cli" }]</c>.</summary>
    public static IReadOnlyList<AgentDescriptor> Parse(string json) =>
        JsonSerializer.Deserialize(json, AgentJsonContext.Default.ListAgentDescriptor)
        ?? throw new InvalidOperationException("The agents file lists no agents");

    /// <summary>The CLI to run for agent <paramref name="id"/>: its full path from the agents file, else its plain name.</summary>
    public static string CliOf(IReadOnlyList<AgentDescriptor> agents, string id) =>
        agents.FirstOrDefault(agent => agent.Id == id)?.Cli ?? id;

    /// <summary>A fixed descriptor validation result; no provider or credential material is returned.</summary>
    public string? Invalid() => string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name)
        || string.IsNullOrWhiteSpace(Command) ? "invalid_descriptor" : null;

    public AgentCommand ToCommand(string workingDirectory) => new(Command, Args, workingDirectory, Env);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<AgentDescriptor>))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;
