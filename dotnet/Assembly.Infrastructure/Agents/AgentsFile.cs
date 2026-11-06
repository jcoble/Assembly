using System.Collections;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Topics;
using Assembly.Application.Features.Agents;

namespace Assembly.Infrastructure.Agents;

/// <summary>The actual descriptor file and its live catalogue. All descriptor writers share its permanent .lock sidecar.</summary>
public sealed class AgentsFile : IReadOnlyList<AgentDescriptor>
{
    private sealed record Snapshot(string Revision, IReadOnlyList<AgentDescriptor> Agents);
    private sealed record Owner(string Machine, ITopics Topics);
    private Snapshot _snapshot = new("", []);
    private Owner? _owner;
    public const string Topic = "provider_runtime";

    public AgentsFile(string path)
    {
        Path = CanonicalPath(path);
        using var held = TakeLock();
        var (_, snapshot) = ReadCurrent();
        Volatile.Write(ref _snapshot, snapshot);
    }

    /// <summary>
    /// For a host starting up: an agents file it can't use is a reason not to start, said plainly, rather than a crash that
    /// leaves a core file each time the service tries again. Null means it said why.
    /// </summary>
    public static AgentsFile? OpenOrSay(string path, TextWriter error)
    {
        if (!File.Exists(path))
        {
            error.WriteLine($"There's no agents file at {path}, so this host didn't start.");
            return null;
        }
        try { return new AgentsFile(path); }
        catch (Exception failure) when (failure is CallFailed or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"The agents file {path} can't be used ({(failure as CallFailed)?.Reason ?? failure.Message}), so this host didn't start.");
            return null;
        }
    }

    public void BindOwner(string machine, ITopics topics)
    {
        if (string.IsNullOrEmpty(machine) || topics is null) throw Refusal("configuration_unavailable");
        var bound = Interlocked.CompareExchange(ref _owner, new Owner(machine, topics), null);
        if (bound is not null && (bound.Machine != machine || !ReferenceEquals(bound.Topics, topics)))
            throw Refusal("configuration_unavailable");
    }

    /// <summary>Safe in-memory topic state; never takes the descriptor file lock.</summary>
    public ProviderRuntimeChanged Current() => new(RequireOwner().Machine, Revision);

    private Owner RequireOwner() => Volatile.Read(ref _owner) ?? throw Refusal("configuration_unavailable");

    public string Path { get; }
    public string LockPath => Path + ".lock";
    public string Revision => Volatile.Read(ref _snapshot).Revision;
    public int Count => Volatile.Read(ref _snapshot).Agents.Count;
    public AgentDescriptor this[int index] => Volatile.Read(ref _snapshot).Agents[index];
    public IEnumerator<AgentDescriptor> GetEnumerator() => Volatile.Read(ref _snapshot).Agents.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Refresh after the owning install/update operation publishes under the same sidecar lock.</summary>
    public string Reload()
    {
        RequireOwner();
        using var held = TakeLock();
        var (_, snapshot) = ReadCurrent();
        SetSnapshot(snapshot);
        return snapshot.Revision;
    }

    public ProviderRuntimeReply Read(string provider, CancellationToken ct = default)
    {
        RequireOwner();
        ct.ThrowIfCancellationRequested();
        using var held = TakeLock();
        var (_, snapshot) = ReadCurrent();
        var agent = Selected(snapshot, provider);
        var reply = Reply(agent, snapshot.Revision);
        SetSnapshot(snapshot);
        return reply;
    }

    public ProviderRuntimeReply Save(string provider, string expectedRevision, ProviderRuntimeSettings settings,
        CancellationToken ct = default)
    {
        RequireOwner();
        ct.ThrowIfCancellationRequested();
        using var held = TakeLock();
        var (document, current) = ReadCurrent();
        if (expectedRevision != current.Revision) throw Refusal("runtime_settings_conflict");
        var agent = Selected(current, provider);
        ValidateChange(agent, settings);
        var entry = document.OfType<JsonObject>().Single(node => node["id"]!.GetValue<string>() == provider);
        if (settings.BinaryPath != agent.Cli)
        {
            if (settings.BinaryPath is null) entry.Remove("cli");
            else entry["cli"] = settings.BinaryPath;
        }
        var variable = AccountContext.ConfigHomeVariable(agent.VendorFamily);
        if (variable is null)
        {
            if (settings.ConfigHome is not null) throw Refusal("runtime_home_unsupported");
        }
        else if (settings.ConfigHome is null)
        {
            if (entry["env"] is JsonObject existing) existing.Remove(variable);
        }
        else
        {
            var environment = entry["env"] as JsonObject;
            if (environment is null) entry["env"] = environment = new JsonObject();
            environment[variable] = settings.ConfigHome;
        }
        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        var replacement = Parse(bytes);
        var reply = Reply(Selected(replacement, provider), replacement.Revision);
        ct.ThrowIfCancellationRequested();
        Publish(bytes, ct);
        // After rename, cancellation cannot turn a committed save into an apparent refusal.
        SetSnapshot(replacement);
        return reply;
    }

    private static void ValidateChange(AgentDescriptor agent, ProviderRuntimeSettings settings)
    {
        if (settings is null || settings.LaunchArguments is null || settings.LaunchArguments.Any(value => value is null))
            throw Refusal("runtime_settings_invalid");
        if (settings.BinaryPath != agent.Cli &&
            (AccountContext.ExecutableVariable(agent.VendorFamily) is null || settings.BinaryPath is { Length: 0 }))
            throw Refusal("runtime_binary_unsupported");
        if (!(agent.CliArgs ?? []).SequenceEqual(settings.LaunchArguments)) throw Refusal("runtime_arguments_unsupported");
        if (settings.ShadowHome != agent.ShadowHome) throw Refusal("runtime_shadow_unsupported");
    }

    private ProviderRuntimeReply Reply(AgentDescriptor agent, string revision)
    {
        var variable = AccountContext.ConfigHomeVariable(agent.VendorFamily);
        var home = variable is not null && agent.Env?.TryGetValue(variable, out var value) == true ? value : null;
        var environment = ConfigTargetIdentity.EnvironmentOf(agent);
        return new(RequireOwner().Machine, agent.Id, revision, ConfigTargetIdentity.Of(agent, environment),
            AccountContext.Of(agent, key => environment.GetValueOrDefault(key)),
            new(agent.Cli, home, agent.ShadowHome, agent.CliArgs?.ToArray() ?? []));
    }

    private void SetSnapshot(Snapshot snapshot)
    {
        var owner = RequireOwner();
        var previous = Volatile.Read(ref _snapshot);
        Volatile.Write(ref _snapshot, snapshot);
        if (previous.Revision != snapshot.Revision)
            owner.Topics.Publish(Topic, owner.Machine, JsonSerializer.Serialize(new ProviderRuntimeChanged(owner.Machine, snapshot.Revision),
                ProviderRuntimeJson.Default.ProviderRuntimeChanged));
    }

    private static AgentDescriptor Selected(Snapshot snapshot, string provider) =>
        snapshot.Agents.SingleOrDefault(agent => agent.Id == provider) ?? throw Refusal("not_found");

    private (JsonArray Document, Snapshot Snapshot) ReadCurrent()
    {
        try
        {
            var bytes = File.ReadAllBytes(Path);
            var snapshot = Parse(bytes);
            var document = JsonNode.Parse(bytes) as JsonArray ?? throw Refusal("runtime_settings_invalid");
            return (document, snapshot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Refusal("runtime_settings_unavailable"); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw Refusal("runtime_settings_invalid"); }
    }

    private static Snapshot Parse(byte[] bytes)
    {
        var agents = AgentDescriptor.Parse(Encoding.UTF8.GetString(bytes));
        if (agents.Any(agent => agent is null || agent.Invalid() is not null || agent.Args is null) ||
            agents.Select(agent => agent.Id).Distinct(StringComparer.Ordinal).Count() != agents.Count)
            throw Refusal("runtime_settings_invalid");
        var frozen = agents.Select(agent => agent with
        {
            Args = Array.AsReadOnly(agent.Args.ToArray()),
            Air = agent.Air is null ? null : Array.AsReadOnly(agent.Air.ToArray()),
            CliArgs = agent.CliArgs is null ? null : Array.AsReadOnly(agent.CliArgs.ToArray()),
            Env = agent.Env is null ? null : new ReadOnlyDictionary<string, string>(
                agent.Env.ToDictionary(pair => pair.Key, pair => pair.Value, ConfigTargetIdentity.EnvironmentComparer))
        }).ToArray();
        return new(Convert.ToHexStringLower(SHA256.HashData(bytes)), Array.AsReadOnly(frozen));
    }

    private FileStream TakeLock()
    {
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            return new FileStream(LockPath, options);
        }
        catch (IOException) { throw Refusal("runtime_settings_busy"); }
        catch (UnauthorizedAccessException) { throw Refusal("runtime_settings_unavailable"); }
    }

    private void Publish(byte[] bytes, CancellationToken ct)
    {
        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, options))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Refusal("runtime_settings_unavailable"); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string CanonicalPath(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var current = System.IO.Path.GetPathRoot(full)!;
        var parts = full[current.Length..].Split(System.IO.Path.DirectorySeparatorChar);
        for (var index = 0; index < parts.Length; index++)
        {
            current = System.IO.Path.Combine(current, parts[index]);
            FileSystemInfo info = index == parts.Length - 1 ? new FileInfo(current) : new DirectoryInfo(current);
            current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }
        return current;
    }

    private static CallFailed Refusal(string reason) => new("Provider runtime settings are unavailable.", reason);
}
