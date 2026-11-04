using System.Net;
using System.Net.Sockets;

namespace Assembly.Infrastructure.Remote;

/// <summary>One callback forward, at the provider's exact port, with no restart or owner retargeting.</summary>
public sealed class LoopbackForward : IAsyncDisposable
{
    private readonly RemoteTunnel _tunnel;
    private readonly TaskCompletionSource _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private Task? _dispose;
    private bool _disposing;
    public int Port { get; }
    public int Pid => _tunnel.Pid;
    public Task Lost => _lost.Task;
    public Task Completion { get; }

    private LoopbackForward(RemoteTunnel tunnel, int port)
    {
        (_tunnel, Port) = (tunnel, port);
        _tunnel.Exited += OnExited;
        Completion = ObserveAsync();
    }

    public static async Task<LoopbackForward> OpenAsync(string sshDestination, string callbackHost, int port, TextWriter log,
        CancellationToken cancel, Func<IReadOnlyList<(string Bind, string Target)>, int, RemoteTunnel>? start = null)
    {
        cancel.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sshDestination) || port is < 1 or > 65535) throw new ArgumentException("Invalid callback forward.");
        (string Bind, string Target)[] forwards = callbackHost switch
        {
            "127.0.0.1" => [("127.0.0.1", "127.0.0.1")],
            "[::1]" => [("[::1]", "[::1]")],
            "localhost" => [("127.0.0.1", "localhost"), ("[::1]", "localhost")],
            _ => throw new ArgumentException("Invalid callback forward.")
        };
        foreach (var forward in forwards)
        {
            using var listener = new TcpListener(IPAddress.Parse(forward.Bind.Trim('[', ']')), port);
            try { listener.Start(); }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
            { throw new CallbackPortInUse(port, sshDestination); }
        }
        var tunnel = start is null ? RemoteTunnel.ForwardOnce(sshDestination, forwards, port, log) : start(forwards, port);
        var opened = new LoopbackForward(tunnel, port);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            if (!await tunnel.ForwardReady.WaitAsync(deadline.Token)) throw Unavailable();
            // ssh's ready event is emitted after all requested listeners bind. Verify each once, without a polling loop.
            foreach (var forward in forwards)
            {
                using var client = new TcpClient(IPAddress.Parse(forward.Bind.Trim('[', ']')).AddressFamily);
                await client.ConnectAsync(IPAddress.Parse(forward.Bind.Trim('[', ']')), port, deadline.Token);
            }
            cancel.ThrowIfCancellationRequested();
            if (tunnel.Completion.IsCompleted) throw Unavailable();
            return opened;
        }
        catch
        {
            await opened.DisposeAsync();
            throw;
        }
    }

    private void OnExited() { lock (_gate) if (!_disposing) _lost.TrySetResult(); }

    private async Task ObserveAsync()
    {
        try { await _tunnel.Completion; }
        finally { lock (_gate) if (!_disposing) _lost.TrySetResult(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposing = true;
            _tunnel.Exited -= OnExited;
            return new(_dispose ??= CloseAsync());
        }
    }

    private async Task CloseAsync()
    {
        try { await _tunnel.DisposeAsync(); }
        finally { await Completion; }
    }

    private static IOException Unavailable() => new("The sign-in callback forward is unavailable.");
}

public sealed class CallbackPortInUse(int port, string host)
    : Exception($"port {port} is in use on this machine, so the sign-in callback cannot reach {host}")
{
    public int Port { get; } = port;
}
