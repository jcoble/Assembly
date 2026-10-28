using System.Diagnostics;
using System.Net.Sockets;
using System.Net;
using System.Runtime.InteropServices;

namespace Assembly.Infrastructure.Remote;

/// <summary>
/// The ssh forward to a remote machine's API, run by this process: <c>127.0.0.1:localPort</c> here reaches
/// <c>127.0.0.1:remotePort</c> there. ssh runs under a perl wrapper that leads its own process group, so a terminal's
/// Ctrl+C or a signal to this process's group never reaches it, and this process stops the group: on dispose and on
/// exit. The wrapper is the backstop for an exit this process can't see (a crash, SIGKILL): once its parent is gone
/// it kills the group. A dead link is noticed by ssh's keepalive and a failed forward ends ssh; either way it's started
/// again, with backoff.
/// </summary>
public sealed class RemoteTunnel : IAsyncDisposable
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly string _file;
    private readonly string[] _args;
    private readonly TextWriter _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _supervising;
    private readonly TaskCompletionSource<bool> _forwardReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pid;
    private int _disposed;
    // The wait before ssh starts again, while there is one: TryNow cuts it short.
    private CancellationTokenSource? _waiting;

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

    /// <summary>Raised each time the command exits on its own.</summary>
    public event Action? Exited;

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
                "ssh", "-v", "-N", "-n",
                "-o", "BatchMode=yes", "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3",
                "-L", $"127.0.0.1:{localPort}:127.0.0.1:{remotePort}", host,
            ],
            log);

    /// <summary>
    /// Starts an independent loopback forward and reads the remote token. Cancellation stops setup and its processes.
    /// </summary>
    public static async Task<(RemoteTunnel Tunnel, RemoteConfig Config)> ConnectAsync(string host, int remotePort, string tokenFile, TextWriter log,
        CancellationToken cancellationToken = default, Func<int, RemoteTunnel>? start = null, Func<CancellationToken, Task<string>>? readToken = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var tunnel = start is null ? ForSsh(host, localPort, remotePort, log) : start(localPort);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            var token = await (readToken is null ? ReadTokenAsync(host, tokenFile, limit.Token) : readToken(limit.Token));
            if (!await tunnel._forwardReady.Task.WaitAsync(limit.Token))
                throw new InvalidOperationException("ssh exited before establishing its forward");
            await WaitForPortAsync(localPort, TimeSpan.FromSeconds(15), limit.Token);
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
        foreach (var arg in new[] { "-o", "BatchMode=yes", host, $"cat -- '{tokenFile.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'" })
            start.ArgumentList.Add(arg);
        using var ssh = Process.Start(start)!;
        using var cancelled = cancellationToken.Register(() => { try { ssh.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var errors = ssh.StandardError.ReadToEndAsync(CancellationToken.None);
        string output;
        try
        {
            output = await ssh.StandardOutput.ReadToEndAsync(cancellationToken);
            await ssh.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            try { if (!ssh.HasExited) ssh.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await ssh.WaitForExitAsync(CancellationToken.None);
            await errors;
        }
        const string prefix = "ASSEMBLY_TOKEN=";
        return output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..]
            ?? throw new InvalidOperationException($"{host} has no token in ~/{tokenFile} (ssh exit {ssh.ExitCode})");
    }

    /// <summary>Waits until the forward accepts connections.</summary>
    public static async Task WaitForPortAsync(int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port, cancellationToken);
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private async Task<string> ReadErrorsAsync(Process process)
    {
        var errors = new System.Text.StringBuilder();
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            // With ExitOnForwardFailure, ssh reaches this point only after its local forwards bind.
            if (line == "debug1: Entering interactive session.") _forwardReady.TrySetResult(true);
            errors.AppendLine(line);
        }
        return errors.ToString();
    }

    private async Task SuperviseAsync()
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!_stopping.IsCancellationRequested)
        {
            // Its own stdout and stdin, so after a hard exit here an orphaned ssh doesn't hold this process's output open.
            var start = new ProcessStartInfo(_file)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
            };
            foreach (var arg in _args)
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            Volatile.Write(ref _pid, process.Id);
            Starts++;
            var startedAt = DateTime.UtcNow;
            var errors = ReadErrorsAsync(process);
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            process.StandardInput.Close();
            try
            {
                await process.WaitForExitAsync(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                // The process may not have established its group yet; stop its owned tree as well.
                Kill(Signal.Kill);
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            Volatile.Write(ref _pid, 0);
            await errors;
            _forwardReady.TrySetResult(false);
            if (_stopping.IsCancellationRequested)
            {
                await output;
                await errors;
                return;
            }
            // A link that held for a while starts again quickly.
            if (DateTime.UtcNow - startedAt > MaxBackoff)
                backoff = TimeSpan.FromSeconds(1);
            Exited?.Invoke();
            await output;
            _log.WriteLine($"tunnel: ssh exited ({process.ExitCode}: {(await errors).Trim()}); again in {backoff.TotalSeconds:0} s");
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token))
            {
                Volatile.Write(ref _waiting, waiting);
                try
                {
                    await Task.Delay(backoff, waiting.Token);
                }
                catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
                {
                    // TryNow: start again now.
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                finally
                {
                    Volatile.Write(ref _waiting, null);
                }
            }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    /// <summary>Starts ssh again now if it's waiting to (Connect); nothing while it runs.</summary>
    public void TryNow()
    {
        try
        {
            Volatile.Read(ref _waiting)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The wait ended as this read it.
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit -= OnExit;
        AppDomain.CurrentDomain.UnhandledException -= OnExit;
        await _stopping.CancelAsync();
        await _supervising;
        _stopping.Dispose();
    }

    private enum Signal { Kill = 9 }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
