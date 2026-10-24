using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Assembly.Mac.Remote;

/// <summary>
/// The ssh forward to a remote machine's API, run by this process: <c>127.0.0.1:localPort</c> here reaches
/// <c>127.0.0.1:remotePort</c> there. ssh runs under a perl wrapper that leads its own process group, so a terminal's
/// Ctrl+C or a signal to this process's group never reaches it, and this process stops the group: on dispose and on
/// exit. The wrapper is the backstop for an exit this process can't see (a crash, SIGKILL): once its parent is gone
/// it kills the group. A dead link is noticed by ssh's keepalive and a failed forward ends ssh; either way it's started
/// again, with backoff.
/// </summary>
internal sealed class RemoteTunnel : IAsyncDisposable
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly string _file;
    private readonly string[] _args;
    private readonly TextWriter _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _supervising;
    private int _pid;

    public RemoteTunnel(string file, IReadOnlyList<string> args, TextWriter log)
    {
        _file = file;
        _args = [.. args];
        _log = log;
        AppDomain.CurrentDomain.ProcessExit += OnExit;
        AppDomain.CurrentDomain.UnhandledException += OnExit;
        _supervising = Task.Run(SuperviseAsync);
    }

    /// <summary>The pid of the command running now (the wrapper's, which is its group's id), or 0 between starts.</summary>
    public int Pid => Volatile.Read(ref _pid);

    /// <summary>How many times ssh has been started.</summary>
    public int Starts { get; private set; }

    // Leads a new group, runs the command in it, and exits as the command did; once this process (its parent) is gone,
    // it kills the group. SIGCHLD wakes the sleep, so the command's exit is seen at once.
    private const string Wrapper =
        """use POSIX ":sys_wait_h"; setpgrp(0, 0); my $parent = getppid(); $SIG{CHLD} = sub {}; """
        + """defined(my $child = fork) or die "fork: $!\n"; if (!$child) { exec { $ARGV[0] } @ARGV or die "exec $ARGV[0]: $!\n" } """
        + """while (waitpid($child, WNOHANG) == 0) { kill "KILL", -$$ if getppid() != $parent; sleep 1 } """
        + """exit($? & 127 ? 128 + ($? & 127) : $? >> 8);""";

    /// <summary>Runs <paramref name="command"/> under the wrapper: in its own group, which ends when this process does.</summary>
    public static RemoteTunnel Wrapped(IReadOnlyList<string> command, TextWriter log) =>
        new("/usr/bin/perl", ["-e", Wrapper, "--", .. command], log);

    public static RemoteTunnel ForSsh(string host, int localPort, int remotePort, TextWriter log) =>
        Wrapped(
            [
                "ssh", "-N",
                "-o", "BatchMode=yes", "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3",
                "-L", $"127.0.0.1:{localPort}:127.0.0.1:{remotePort}", host,
            ],
            log);

    /// <summary>
    /// Starts the tunnel to <paramref name="host"/>'s API on <paramref name="remotePort"/>, reached here on that port
    /// plus 10000, and reads its token from <paramref name="tokenFile"/> there.
    /// </summary>
    public static async Task<(RemoteTunnel Tunnel, RemoteConfig Config)> ConnectAsync(string host, int remotePort, string tokenFile, TextWriter log)
    {
        var localPort = remotePort + 10000;
        var tunnel = ForSsh(host, localPort, remotePort, log);
        try
        {
            var token = await ReadTokenAsync(host, tokenFile, CancellationToken.None);
            await WaitForPortAsync(localPort, TimeSpan.FromSeconds(15));
            log.WriteLine($"tunnel: {host} on 127.0.0.1:{localPort} (ssh pid {tunnel.Pid})");
            return (tunnel, new RemoteConfig(host, new Uri($"http://127.0.0.1:{localPort}"), token));
        }
        catch
        {
            await tunnel.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The remote's API token, from its token file, over ssh. Kept in memory only: it is never in an argument, a URL,
    /// a log line or a file here.
    /// </summary>
    public static async Task<string> ReadTokenAsync(string host, string tokenFile, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("ssh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-o", "BatchMode=yes", host, $"cat {tokenFile}" })
            start.ArgumentList.Add(arg);
        using var ssh = Process.Start(start)!;
        var output = await ssh.StandardOutput.ReadToEndAsync(cancellationToken);
        await ssh.WaitForExitAsync(cancellationToken);
        const string prefix = "ASSEMBLY_TOKEN=";
        return output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..]
            ?? throw new InvalidOperationException($"{host} has no token in ~/{tokenFile} (ssh exit {ssh.ExitCode})");
    }

    /// <summary>Waits until the forward accepts connections.</summary>
    public static async Task WaitForPortAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }
    }

    private async Task SuperviseAsync()
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!_stopping.IsCancellationRequested)
        {
            var start = new ProcessStartInfo(_file) { UseShellExecute = false, RedirectStandardError = true };
            foreach (var arg in _args)
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            Volatile.Write(ref _pid, process.Id);
            Starts++;
            var startedAt = DateTime.UtcNow;
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            Volatile.Write(ref _pid, 0);
            // A link that held for a while starts again quickly.
            if (DateTime.UtcNow - startedAt > MaxBackoff)
                backoff = TimeSpan.FromSeconds(1);
            _log.WriteLine($"tunnel: ssh exited ({process.ExitCode}: {(await errors).Trim()}); again in {backoff.TotalSeconds:0} s");
            try
            {
                await Task.Delay(backoff, _stopping.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private void OnExit(object? sender, EventArgs e) => Kill(Signal.Kill);

    private void Kill(Signal signal)
    {
        if (Pid is var pid and > 0)
            _ = kill(-pid, (int)signal);
    }

    public async ValueTask DisposeAsync()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnExit;
        AppDomain.CurrentDomain.UnhandledException -= OnExit;
        var pid = Pid;
        await _stopping.CancelAsync();
        if (pid > 0)
        {
            _ = kill(-pid, (int)Signal.Term);
            for (var i = 0; i < 20 && kill(pid, 0) == 0; i++)
                await Task.Delay(100);
            // Whatever of the group is left.
            _ = kill(-pid, (int)Signal.Kill);
        }
        await _supervising;
        _stopping.Dispose();
    }

    private enum Signal
    {
        Kill = 9,
        Term = 15,
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
