using System.Diagnostics;
using System.Text.Json.Serialization;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Remote.Install;

namespace Assembly.Infrastructure.Remote;

/// <summary>A host to offer when adding a machine. The name beside it is only for reading; picking one asks SSH itself.</summary>
internal sealed record SshHost(string Host, string? HostName);
internal sealed record SshHostList(IReadOnlyList<SshHost> Hosts);
/// <summary>What SSH says it would use for a host.</summary>
internal sealed record SshTarget(string Host, string User, string HostName, int Port);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SshHostsArgs;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SshTargetArgs([property: JsonRequired] string Host);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SshHostsArgs))]
[JsonSerializable(typeof(SshTargetArgs))]
[JsonSerializable(typeof(SshHostList))]
[JsonSerializable(typeof(SshTarget))]
internal sealed partial class SshHostsJson : JsonSerializerContext;

/// <summary>
/// The hosts this account already knows: its SSH config's aliases, then known_hosts' others. The files are read when the
/// form asks, and nothing watches them. A pattern or a hashed entry names no host a person could pick.
/// </summary>
internal static class SshHosts
{
    private const int MaxIncludes = 16;

    internal static IReadOnlyList<SshHost> Read(string home)
    {
        var ssh = Path.Combine(home, ".ssh");
        var found = new Dictionary<string, SshHost>(StringComparer.Ordinal);
        var order = new List<string>();
        Config(Path.Combine(ssh, "config"), ssh, home, new(StringComparer.Ordinal), found, order, 0);
        foreach (var host in KnownHosts(Path.Combine(ssh, "known_hosts")))
            if (found.TryAdd(host, new(host, null))) order.Add(host);
        return [.. order.Select(host => found[host])];
    }

    private static void Config(string file, string ssh, string home, HashSet<string> seen, Dictionary<string, SshHost> found, List<string> order, int depth)
    {
        if (depth > MaxIncludes || !seen.Add(file)) return;
        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        var current = new List<string>();
        foreach (var line in lines)
        {
            var words = Words(line);
            if (words.Count < 2) continue;
            switch (words[0].ToLowerInvariant())
            {
                case "host":
                    current = [.. words.Skip(1).Where(Plain)];
                    foreach (var host in current)
                        if (found.TryAdd(host, new(host, null))) order.Add(host);
                    break;
                case "match":
                    current = [];
                    break;
                case "hostname":
                    foreach (var host in current)
                        if (found[host].HostName is null) found[host] = found[host] with { HostName = words[1] };
                    break;
                case "include":
                    foreach (var pattern in words.Skip(1))
                        foreach (var included in Matching(pattern, ssh, home))
                            Config(included, ssh, home, seen, found, order, depth + 1);
                    break;
                default:
                    break;
            }
        }
    }

    // A relative Include is under ~/.ssh, as SSH reads it.
    private static IEnumerable<string> Matching(string pattern, string ssh, string home)
    {
        var path = pattern.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, pattern[2..]) : Path.Combine(ssh, pattern);
        var folder = Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder)) return [];
        try { return Directory.GetFiles(folder, Path.GetFileName(path)).Order(StringComparer.Ordinal); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> KnownHosts(string file)
    {
        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var line in lines)
        {
            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var names = words.Length > 0 && words[0].StartsWith('@') ? words.ElementAtOrDefault(1) : words.ElementAtOrDefault(0);
            if (names is null || names.StartsWith('#') || names.StartsWith("|1|", StringComparison.Ordinal)) continue;
            foreach (var name in names.Split(','))
            {
                var host = name.StartsWith('[') && name.IndexOf(']', StringComparison.Ordinal) is > 0 and var end ? name[1..end] : name;
                if (Plain(host)) yield return host;
            }
        }
    }

    private static bool Plain(string host) => host.Length > 0 && !host.Contains('*', StringComparison.Ordinal) && !host.Contains('?', StringComparison.Ordinal) && !host.StartsWith('!');

    // "Key value", "Key=value" and quoted words, the forms ssh_config allows; a # outside quotes ends the line.
    private static List<string> Words(string line)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in line.Trim())
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && c == '#') break;
            if (!quoted && (char.IsWhiteSpace(c) || (c == '=' && words.Count == 0)))
            {
                if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
                continue;
            }
            word.Append(c);
        }
        if (word.Length > 0) words.Add(word.ToString());
        return words;
    }

    /// <summary>Reads what SSH itself decides for a host, so its rules are never second-guessed here.</summary>
    internal static async Task<SshTarget> TargetAsync(string host, string? configFile, CancellationToken ct)
    {
        if (!RemoteReleaseInstall.SafeTarget().IsMatch(host)) throw new CallFailed("That SSH host isn't valid.", "bad_args");
        var start = new ProcessStartInfo("ssh") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-G");
        if (configFile is not null) { start.ArgumentList.Add("-F"); start.ArgumentList.Add(configFile); }
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(host);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        Process? started;
        try { started = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception error) { throw new CallFailed($"SSH could not start: {error.Message}", "ssh_unavailable"); }
        using var process = started ?? throw new CallFailed("SSH could not start.", "ssh_unavailable");
        process.StandardInput.Close();
        var said = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new CallFailed("SSH didn't answer about that host.", "ssh_unavailable");
        }
        if (process.ExitCode != 0) throw new CallFailed($"SSH can't read that host: {(await errors).Trim()}", "ssh_unavailable");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in (await said).Split('\n'))
            if (line.Split(' ', 2) is [var key, var value]) values.TryAdd(key, value.Trim());
        return values.TryGetValue("user", out var user) && values.TryGetValue("hostname", out var name) &&
            values.TryGetValue("port", out var port) && int.TryParse(port, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? new(host, user, name, number) : throw new CallFailed("SSH's answer about that host couldn't be read.", "ssh_unavailable");
    }
}

/// <summary>The form's two questions: which hosts, and what one of them means.</summary>
internal sealed class SshHostsFeature(string home, string? configFile = null) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        calls.Read("sshHosts", "list", SshHostsJson.Default.SshHostsArgs, SshHostsJson.Default.SshHostList, (_, call) =>
        {
            Owner(call);
            return Task.Run(() => new SshHostList(SshHosts.Read(home)), call.Cancellation);
        });
        calls.Read("sshHosts", "target", SshHostsJson.Default.SshTargetArgs, SshHostsJson.Default.SshTarget, (args, call) =>
        {
            Owner(call);
            return SshHosts.TargetAsync(args.Host, configFile, call.Cancellation);
        });
    }

    private static void Owner(CallContext call)
    { if (call.Peer is not OwnerPeer) throw new CallFailed("Owner access is required.", "forbidden"); }
}
