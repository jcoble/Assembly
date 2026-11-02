using System.Collections;
using System.Security.Cryptography;
using System.Text;
using Assembly.Application.Common.Calls;

namespace Assembly.Application.Common.Agents;

public sealed record ConfigTarget(string Id, string Name, string? TargetId);

/// <summary>Opaque identity of the exact descriptor and effective launch environment.</summary>
public static class ConfigTargetIdentity
{
    public static StringComparer EnvironmentComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static IReadOnlyDictionary<string, string> EnvironmentOf(AgentDescriptor descriptor)
    {
        var values = new Dictionary<string, string>(EnvironmentComparer);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            values[(string)entry.Key] = (string)entry.Value!;
        foreach (var (key, value) in descriptor.Env ?? new Dictionary<string, string>()) values[key] = value;
        return values;
    }

    public static string Of(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> effectiveEnvironment)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("assembly-composer-target-v1");
        writer.Write(descriptor.Id);
        writer.Write(descriptor.Command);
        writer.Write(descriptor.Args.Count);
        foreach (var arg in descriptor.Args) writer.Write(arg);
        writer.Write(descriptor.Air?.Count ?? 0);
        foreach (var air in descriptor.Air ?? []) writer.Write(air);
        var canonical = new Dictionary<string, string>(EnvironmentComparer);
        foreach (var (key, value) in effectiveEnvironment) canonical[key] = value;
        writer.Write(canonical.Count);
        foreach (var (key, value) in canonical.OrderBy(x => x.Key, EnvironmentComparer))
        {
            writer.Write(OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key);
            writer.Write(value);
        }
        writer.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    /// <summary>Validates the current explicit configuration against an already-captured environment.</summary>
    public static void Validate(IAgentLauncher launcher, AgentDescriptor descriptor,
        IReadOnlyDictionary<string, string> effectiveEnvironment)
    {
        var current = RequireSameConfiguration(launcher, descriptor, effectiveEnvironment);
        if (current.Invalid() is not null)
            throw new CallFailed("The selected agent configuration changed. Select it again.");
    }

    /// <summary>Checks exact captured configuration identity without treating observation as permission to launch.</summary>
    public static AgentDescriptor RequireSameConfiguration(IAgentLauncher launcher, AgentDescriptor descriptor,
        IReadOnlyDictionary<string, string> effectiveEnvironment)
    {
        var current = launcher.Agents.FirstOrDefault(agent => agent.Id == descriptor.Id);
        if (current is null || current.Cli != descriptor.Cli
            || current.VendorFamily != descriptor.VendorFamily
            || ConfigTargetIdentity.Of(current, effectiveEnvironment) != ConfigTargetIdentity.Of(descriptor, effectiveEnvironment)
            || current.Env?.Any(pair => !effectiveEnvironment.TryGetValue(pair.Key, out var value) || value != pair.Value) == true)
            throw new CallFailed("The selected agent configuration changed. Select it again.");
        return current;
    }

    public static (AgentDescriptor Descriptor, IReadOnlyDictionary<string, string> Environment, string TargetId) Capture(IAgentLauncher launcher, string agent, string? expected = null)
    {
        var descriptor = launcher.Agents.FirstOrDefault(x => x.Id == agent) ?? throw new CallFailed("This machine no longer has the selected agent.");
        // Freeze descriptor collections too: a captured launch must not observe a caller mutating a list later.
        descriptor = descriptor with
        {
            Args = descriptor.Args.ToArray(),
            Air = descriptor.Air?.ToArray(),
            Env = descriptor.Env?.ToDictionary(x => x.Key, x => x.Value, EnvironmentComparer)
        };
        var environment = EnvironmentOf(descriptor);
        var target = Of(descriptor, environment);
        if (expected is not null && expected != target) throw new CallFailed("The selected agent configuration changed. Select it again.");
        return (descriptor, environment, target);
    }
}
