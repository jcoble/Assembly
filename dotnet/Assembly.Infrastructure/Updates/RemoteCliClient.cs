using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;

namespace Assembly.Infrastructure.Updates;

// Obtained from the owning installation/profile authority, never caller-supplied check arguments.
// No descriptor/environment or authentication token is copied off the remote.
internal sealed record RemoteCliBinding(SavedMachine Profile, string MachineId, string InstallationId,
    string CaptureFile, AgentCommand HelperCommand, string VendorFamily, string ProviderId,
    string AccountContext, string ConfigurationTargetId);

internal sealed class RemoteCliClient(HttpClient http, TimeProvider time,
    Func<SshCliReadInvocation, CancellationToken, CancellationToken, Task<byte[]>>? readProcess = null)
{
    internal async Task<CliCheckResult> CheckAsync(RemoteCliBinding binding, CliCheckArgs request,
        Func<RemoteCliBinding?> currentBinding, CancellationToken caller, CancellationToken hostStop)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20), time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(caller, hostStop, deadline.Token);
        try { return await CheckCapturedAsync(binding, request, currentBinding, stop.Token, hostStop); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            caller.ThrowIfCancellationRequested(); hostStop.ThrowIfCancellationRequested();
            return new(request.MachineId, request.ProviderId, request.AccountContext, request.ConfigurationTargetId,
                null, "unknown", "unknown", null, "unverified", "timeout");
        }
    }

    private async Task<CliCheckResult> CheckCapturedAsync(RemoteCliBinding binding, CliCheckArgs request,
        Func<RemoteCliBinding?> currentBinding, CancellationToken caller, CancellationToken hostStop)
    {
        RequireSame();
        CliCandidate? candidate = null; string? releaseReason = null;
        try { candidate = await new CliReleaseSource(http, time).LatestAsync(binding.VendorFamily, caller); }
        catch (CallFailed) { releaseReason = "release_unverified"; }
        RequireSame();
        var command = FixedInvocation(binding);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, UpdatesJson.Default.CliCheckArgs);
        var read = readProcess ?? new SshUpdateProcess(time).ReadAsync;
        var output = await read(new(command, Request: bytes), caller, hostStop);
        RemoteCliReadResult result;
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)) throw Failure("invalid_remote_result");
            result = JsonSerializer.Deserialize(output, RemoteCliJson.Default.RemoteCliReadResult) ?? throw Failure("invalid_remote_result");
        }
        catch (JsonException) { throw Failure("invalid_remote_result"); }
        if (result.MachineId != binding.MachineId || result.InstallationId != binding.InstallationId ||
            result.ProviderId != request.ProviderId || result.AccountContext != request.AccountContext ||
            result.ConfigurationTargetId != request.ConfigurationTargetId ||
            result.InstalledVersion is not null && !CliVersion.IsStable(result.InstalledVersion) ||
            result.Manager is not ("npm" or "unknown") || result.Source is not ("npm" or "unknown") ||
            result.Reason is not (null or "probe_failed" or "unsupported_provider" or "cli_missing" or "unrecognized_version" or
                "installation_changed" or "selection_changed" or "timeout" or "start_failed" or "output_too_large" or "nonzero_exit"))
            throw Failure("invalid_remote_result");
        RequireSame();
        var reason = result.Reason ?? releaseReason;
        var comparison = reason is not null || result.InstalledVersion is null || candidate is null ? "unverified"
            : result.Source != "npm" || result.Manager != "npm" ? "unsupported"
            : CliVersion.Compare(result.InstalledVersion, candidate.Version);
        if (comparison == "unsupported") reason = "unsupported_installation";
        return new(request.MachineId, request.ProviderId, request.AccountContext, request.ConfigurationTargetId,
            result.InstalledVersion, result.Manager, result.Source, candidate, comparison, reason);

        void RequireSame()
        {
            caller.ThrowIfCancellationRequested(); hostStop.ThrowIfCancellationRequested();
            var current = currentBinding();
            if (current is null || current.Profile != binding.Profile || current.MachineId != binding.MachineId ||
                current.InstallationId != binding.InstallationId || current.CaptureFile != binding.CaptureFile ||
                current.VendorFamily != binding.VendorFamily || current.ProviderId != binding.ProviderId ||
                current.AccountContext != binding.AccountContext || current.ConfigurationTargetId != binding.ConfigurationTargetId ||
                current.HelperCommand.FileName != binding.HelperCommand.FileName ||
                current.HelperCommand.WorkingDirectory != binding.HelperCommand.WorkingDirectory ||
                !current.HelperCommand.Arguments.SequenceEqual(binding.HelperCommand.Arguments) ||
                request.MachineId != binding.MachineId || request.ProviderId != binding.ProviderId ||
                request.AccountContext != binding.AccountContext || request.ConfigurationTargetId != binding.ConfigurationTargetId ||
                !CliCommand.Supports(binding.VendorFamily)) throw Failure("selection_changed");
        }
    }

    internal static AgentCommand FixedInvocation(RemoteCliBinding binding)
    {
        var helper = binding.HelperCommand;
        if (!Path.IsPathFullyQualified(binding.CaptureFile) || !Path.IsPathFullyQualified(helper.FileName) ||
            !Path.IsPathFullyQualified(helper.WorkingDirectory) || helper.Environment?.Count > 0 || helper.ReplaceEnvironment ||
            !Guid.TryParseExact(binding.InstallationId, "N", out _) || string.IsNullOrWhiteSpace(binding.Profile.Ssh) ||
            binding.Profile.Ssh.StartsWith('-') || binding.Profile.Ssh.Any(char.IsControl)) throw Failure("configuration_unavailable");
        var words = new[] { helper.FileName }.Concat(helper.Arguments).Concat(["cli-read", "--capture", binding.CaptureFile]);
        var remote = "cd -- " + Quote(helper.WorkingDirectory) + " && exec " + string.Join(' ', words.Select(Quote));
        // Port is the backend listener port. SSH configuration/trust supplies its own port and authentication.
        return new("ssh", ["-T", "-S", "none", "-o", "BatchMode=yes", "-o", "ServerAliveInterval=5",
            "-o", "ServerAliveCountMax=2", "--", binding.Profile.Ssh, remote], Environment.CurrentDirectory);
    }

    private static string Quote(string value)
    {
        if (value.IndexOf('\0') >= 0) throw Failure("configuration_unavailable");
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }
    private static CallFailed Failure(string reason) => new("CLI check unavailable.", reason);
}
