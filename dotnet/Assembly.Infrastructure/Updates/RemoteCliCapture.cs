using System.Security.Cryptography;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;

namespace Assembly.Infrastructure.Updates;

/// <summary>Private owning-host snapshot. Its location is supplied by the host/profile authority, never inferred.</summary>
internal static class RemoteCliCapture
{
    private const int Limit = 4 * 1024 * 1024;

    internal static RemoteCliCaptureData Capture(IAgentLauncher launcher, string machineId, string installationId,
        string home, string agentsFile, AgentCommand helperCommand)
    {
        if (string.IsNullOrWhiteSpace(machineId) || !Guid.TryParseExact(installationId, "N", out _) ||
            !Path.IsPathFullyQualified(home) || !Path.IsPathFullyQualified(agentsFile) ||
            !Path.IsPathFullyQualified(helperCommand.FileName) || !Path.IsPathFullyQualified(helperCommand.WorkingDirectory))
            throw Unavailable();
        var source = ReadSource(agentsFile);
        var configured = AgentDescriptor.Parse(System.Text.Encoding.UTF8.GetString(source));
        var targets = new List<RemoteCliTarget>();
        foreach (var agent in launcher.Agents.Where(a => CliCommand.Supports(a.VendorFamily)))
        {
            var capture = ConfigTargetIdentity.Capture(launcher, agent.Id);
            CliCommand.RequireSelectedConfiguration(launcher, capture.Descriptor, capture.Environment);
            if (configured.Count(a => a.Id == agent.Id) != 1) throw Unavailable();
            var sourceLauncher = new AgentsOnlyLauncher(() => configured);
            CliCommand.RequireSelectedConfiguration(sourceLauncher, capture.Descriptor, capture.Environment);
            targets.Add(new(capture.Descriptor, new(capture.Environment, ConfigTargetIdentity.EnvironmentComparer),
                capture.TargetId, AccountContext.Of(capture.Descriptor, key => capture.Environment.GetValueOrDefault(key))));
        }
        if (!ReadSource(agentsFile).AsSpan().SequenceEqual(source)) throw Unavailable();
        return new(machineId, installationId, home, agentsFile, Hash(source), helperCommand with
        { Arguments = helperCommand.Arguments.ToArray(), Environment = helperCommand.Environment?.ToDictionary(p => p.Key, p => p.Value) }, targets);
    }

    internal static RemoteCliLocator Locator(RemoteCliCaptureData capture, string ownerChosenCaptureFile)
    {
        if (capture.HelperCommand.Environment?.Count > 0 || capture.HelperCommand.ReplaceEnvironment) throw Unavailable();
        var locator = new RemoteCliLocator(capture.MachineId, capture.InstallationId, ownerChosenCaptureFile,
            capture.HelperCommand.FileName, capture.HelperCommand.Arguments.ToArray(), capture.HelperCommand.WorkingDirectory,
            capture.Targets.Select(target => new RemoteCliTargetIdentity(target.Descriptor.Id, target.Descriptor.VendorFamily!,
                target.AccountContext, target.ConfigurationTargetId)).ToArray());
        locator.Validate();
        return locator;
    }

    internal static async Task SaveAsync(string ownerChosenFile, RemoteCliCaptureData capture, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(ownerChosenFile)) throw Unavailable();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(capture, RemoteCliJson.Default.RemoteCliCaptureData);
        if (bytes.Length > Limit) throw Unavailable();
        var temporary = ownerChosenFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(temporary, options))
            {
                await output.WriteAsync(bytes, ct);
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, ownerChosenFile, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static RemoteCliCaptureData Load(string ownerChosenFile)
    {
        if (!Path.IsPathFullyQualified(ownerChosenFile)) throw Unavailable();
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(ownerChosenFile) &
            (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
             UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0) throw Unavailable();
        return JsonSerializer.Deserialize(ReadSource(ownerChosenFile), RemoteCliJson.Default.RemoteCliCaptureData) ?? throw Unavailable();
    }

    internal static IReadOnlyList<AgentDescriptor> ReadCurrentAgents(RemoteCliCaptureData capture)
    {
        var bytes = ReadSource(capture.AgentsFile);
        if (Hash(bytes) != capture.AgentsHash) throw Unavailable();
        return AgentDescriptor.Parse(System.Text.Encoding.UTF8.GetString(bytes));
    }

    private static byte[] ReadSource(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > Limit) throw Unavailable();
        var bytes = new byte[Limit + 1];
        var count = input.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count > Limit) throw Unavailable();
        return bytes[..count];
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static CallFailed Unavailable() => new("CLI check unavailable.", "configuration_unavailable");
}
