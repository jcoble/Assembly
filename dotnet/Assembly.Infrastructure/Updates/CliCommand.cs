using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Common.Processes;

namespace Assembly.Infrastructure.Updates;

internal sealed record CliInstalled(string Version, string Manager, string Source, IReadOnlyList<CliFile> Files);
internal sealed record CliFile(string Path, string Resolved, string Hash);

/// <summary>Only fixed CLI version and installed npm-root reads; never launches an adapter or an update.</summary>
internal sealed class CliCommand(TimeProvider time)
{
    private const int StdoutLimit = 64 * 1024, StderrLimit = 16 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly IReadOnlyDictionary<string, KnownCli> Known = new Dictionary<string, KnownCli>(StringComparer.Ordinal)
    {
        ["codex"] = new("codex", "@openai/codex", "codex-cli ", ""),
        ["claude"] = new("claude", "@anthropic-ai/claude-code", "", " (Claude Code)")
    };

    internal static bool Supports(string? family) => family is not null && Known.ContainsKey(family);

    public async Task<CliInstalled> ProbeAsync(IAgentLauncher launcher, AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20), time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            if (descriptor.VendorFamily is null || !Known.TryGetValue(descriptor.VendorFamily, out var cli)) throw Failure("unsupported_provider");
            var executable = Resolve(descriptor.Cli ?? cli.Name, cli.Name, environment) ?? throw Failure("cli_missing");
            var executableFile = await CaptureFile(executable, stop.Token);
            RequireSelectedConfiguration(launcher, descriptor, environment);
            var output = (await ReadAsync(executable, ["--version"], environment, stop.Token)).TrimEnd('\r', '\n');
            if (!output.StartsWith(cli.Prefix, StringComparison.Ordinal) || !output.EndsWith(cli.Suffix, StringComparison.Ordinal) ||
                output.Length <= cli.Prefix.Length + cli.Suffix.Length) throw Failure("unrecognized_version");
            var version = output[cli.Prefix.Length..(output.Length - cli.Suffix.Length)];
            if (!CliVersion.IsStable(version)) throw Failure("unrecognized_version");
            var files = new List<CliFile> { executableFile };
            var npm = Resolve("npm", "npm", environment);
            if (npm is null) return new(version, "unknown", "unknown", files);
            var npmFile = await CaptureFile(npm, stop.Token);
            RequireSelectedConfiguration(launcher, descriptor, environment);
            var root = (await ReadAsync(npm, ["root", "-g"], environment, stop.Token)).TrimEnd('\r', '\n');
            if (!Path.IsPathFullyQualified(root) || root.Contains('\n') || root.Contains('\r')) return new(version, "unknown", "unknown", files);
            var package = Path.Combine(root, cli.Package);
            var metadata = Path.Combine(package, "package.json");
            if (!File.Exists(metadata) || new FileInfo(metadata).Length > StdoutLimit) return new(version, "unknown", "unknown", files);
            var metadataFile = await CaptureFile(metadata, stop.Token);
            await using var metadataStream = File.OpenRead(metadata);
            var bytes = new byte[StdoutLimit + 1];
            var count = await metadataStream.ReadAtLeastAsync(bytes, bytes.Length, false, stop.Token);
            Array.Resize(ref bytes, count);
            if (bytes.Length > StdoutLimit || Convert.ToHexStringLower(SHA256.HashData(bytes)) != metadataFile.Hash) throw Failure("installation_changed");
            using var json = JsonDocument.Parse(bytes);
            var fields = json.RootElement;
            if (fields.ValueKind != JsonValueKind.Object || fields.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1) ||
                !fields.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != cli.Package ||
                !fields.TryGetProperty("version", out var actual) || actual.ValueKind != JsonValueKind.String || actual.GetString() != version ||
                !fields.TryGetProperty("bin", out var bin) || bin.ValueKind != JsonValueKind.Object ||
                bin.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1) ||
                !bin.TryGetProperty(cli.Name, out var entry) || entry.ValueKind != JsonValueKind.String || entry.GetString() is not { } relative ||
                Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p is "" or "." or ".."))
                return new(version, "unknown", "unknown", files);
            var linked = await CaptureFile(Path.Combine(package, relative), stop.Token);
            if (linked.Resolved != executableFile.Resolved) return new(version, "unknown", "unknown", files);
            files.AddRange([npmFile, metadataFile, linked]);
            var installed = new CliInstalled(version, "npm", "npm", files);
            await RequireUnchangedAsync(installed, stop.Token);
            return installed;
        }
        catch (OperationCanceledException) { ct.ThrowIfCancellationRequested(); throw Failure("timeout"); }
        catch (CallFailed) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or Win32Exception or ArgumentException)
        { throw Failure("probe_failed"); }
    }

    // This local check retains the original explicit map; auth callers may intentionally carry a flattened map.
    internal static void RequireSelectedConfiguration(IAgentLauncher launcher, AgentDescriptor captured,
        IReadOnlyDictionary<string, string> environment)
    {
        var current = ConfigTargetIdentity.RequireSameConfiguration(launcher, captured, environment);
        var expected = new Dictionary<string, string>(captured.Env ?? new Dictionary<string, string>(), ConfigTargetIdentity.EnvironmentComparer);
        var actual = new Dictionary<string, string>(current.Env ?? new Dictionary<string, string>(), ConfigTargetIdentity.EnvironmentComparer);
        if (current.Invalid() is not null || actual.Count != expected.Count ||
            actual.Any(pair => !expected.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw Failure("selection_changed");
    }

    public static async Task RequireUnchangedAsync(CliInstalled installed, CancellationToken ct)
    {
        foreach (var file in installed.Files)
            if (await CaptureFile(file.Path, ct) != file) throw Failure("installation_changed");
    }

    private static string? Resolve(string configured, string bare, IReadOnlyDictionary<string, string> environment)
    {
        if (Path.IsPathFullyQualified(configured)) return Executable(configured) ? configured : null;
        if (configured != bare) return null;
        foreach (var folder in environment.GetValueOrDefault("PATH", "").Split(Path.PathSeparator))
            if (Path.IsPathFullyQualified(folder) && Executable(Path.Combine(folder, bare))) return Path.Combine(folder, bare);
        return null;
    }

    private static bool Executable(string path) => File.Exists(path) && (OperatingSystem.IsWindows() ||
        (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0);

    private static string RealPath(string path)
    {
        path = Path.GetFullPath(path);
        var current = Path.GetPathRoot(path)!;
        foreach (var part in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo item = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (item.LinkTarget is not null) current = item.ResolveLinkTarget(true)?.FullName ?? throw Failure("installation_changed");
        }
        return current;
    }

    private static async Task<CliFile> CaptureFile(string path, CancellationToken ct)
    {
        var resolved = RealPath(path);
        await using var input = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
        if (RealPath(path) != resolved) throw Failure("installation_changed");
        return new(path, resolved, hash);
    }

    private static async Task<string> ReadAsync(string executable, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var command = ProcessGroup.Leading(new(executable, args, Path.GetDirectoryName(executable)!, environment, ReplaceEnvironment: true));
        var info = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = command.WorkingDirectory
        };
        foreach (var arg in command.Arguments) info.ArgumentList.Add(arg);
        info.Environment.Clear(); foreach (var (key, value) in environment) info.Environment[key] = value;
        using var process = Process.Start(info) ?? throw Failure("start_failed");
        process.StandardInput.Close();
        using var overflow = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, overflow.Token);
        var stdout = Drain(process.StandardOutput.BaseStream, StdoutLimit, overflow);
        var stderr = Drain(process.StandardError.BaseStream, StderrLimit, overflow);
        try { await process.WaitForExitAsync(stop.Token); }
        catch (OperationCanceledException) when (overflow.IsCancellationRequested && !ct.IsCancellationRequested) { }
        finally
        {
            ProcessGroup.Kill(process.Id);
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
        if (overflow.IsCancellationRequested) throw Failure("output_too_large");
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw Failure("nonzero_exit");
        return Utf8.GetString(await stdout);
    }

    private static async Task<byte[]> Drain(Stream stream, int limit, CancellationTokenSource overflow)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (await stream.ReadAsync(buffer) is var count && count != 0)
        {
            if (output.Length + count > limit) { overflow.Cancel(); return []; }
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static CallFailed Failure(string reason) => new("CLI check unavailable.", reason);
    private sealed record KnownCli(string Name, string Package, string Prefix, string Suffix);
}
