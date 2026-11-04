using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Updates;

internal sealed record InstalledCli(string Family, string? Version);

/// <summary>What one machine has: each adapter's version and the sources its bundle was packaged from, and each CLI's version.</summary>
internal sealed record InstalledTools(IReadOnlyDictionary<string, string> Adapters, string? Packaging, IReadOnlyList<InstalledCli> Clis);

/// <summary>Answers on every machine, so a remote reports its own tools; old hosts refuse the call and show as unknown.</summary>
internal sealed class InstalledCalls(IAgentLauncher launcher, TimeProvider time) : IFeature
{
    internal const string Manifest = "provider-manifest.json";

    public void Register(ICallRegistry calls) => calls.Read("updates", "installed", UpdatesJson.Default.InstalledArgs, UpdatesJson.Default.InstalledTools,
        (_, call) => call.Peer is OwnerPeer ? ReadAsync(call.Cancellation) : throw new CallFailed("Owner access is required.", "forbidden"));

    internal async Task<InstalledTools> ReadAsync(CancellationToken ct)
    {
        var agents = launcher.Agents;
        var manifests = agents.Select(agent => ManifestOf(agent.Args)).OfType<string>().Distinct(StringComparer.Ordinal);
        var adapters = new Dictionary<string, string>(StringComparer.Ordinal);
        string? packaging = null;
        foreach (var file in manifests)
        {
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(file, ct));
            if (json.RootElement.TryGetProperty("packaging", out var value) && value.ValueKind == JsonValueKind.String) packaging = value.GetString();
            if (json.RootElement.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Object)
                foreach (var version in versions.EnumerateObject())
                    if (version.Value.ValueKind == JsonValueKind.String) adapters[version.Name] = version.Value.GetString()!;
        }
        var clis = await Task.WhenAll(agents.Where(agent => CliCommand.Supports(agent.VendorFamily)).Select(agent => ProbeAsync(agent, ct)));
        return new(adapters, packaging, clis);
    }

    private async Task<InstalledCli> ProbeAsync(AgentDescriptor agent, CancellationToken ct)
    {
        try { return new(agent.VendorFamily!, (await new CliCommand(time).ProbeAsync(launcher, agent, ConfigTargetIdentity.EnvironmentOf(agent), ct)).Version); }
        catch (CallFailed) { return new(agent.VendorFamily!, null); }
    }

    // An adapter's entry sits somewhere inside its bundle; the bundle's manifest is the record of what it was built from.
    internal static string? ManifestOf(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !Path.IsPathFullyQualified(args[0])) return null;
        for (var folder = Path.GetDirectoryName(args[0]); folder is not null; folder = Path.GetDirectoryName(folder))
            if (File.Exists(Path.Combine(folder, Manifest))) return Path.Combine(folder, Manifest);
        return null;
    }
}
