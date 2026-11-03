using System.Net;
using System.Net.Sockets;
using Assembly.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class LoopbackForwardTests
{
    [Theory]
    [InlineData("127.0.0.1", 1)]
    [InlineData("localhost", 2)]
    [InlineData("[::1]", 1)]
    public async Task The_actual_ready_listener_uses_the_same_port_and_only_the_required_loopbacks(string host, int count)
    {
        var port = FreePort();
        IReadOnlyList<(string Bind, string Target)>? actual = null;
        RemoteTunnel? process = null;
        await using var opened = await LoopbackForward.OpenAsync("inert-profile", host, port, TextWriter.Null, default,
            (forwards, selectedPort) =>
            {
                Assert.Equal(port, selectedPort); actual = forwards;
                return process = Listener(forwards, port);
            });
        Assert.NotNull(actual); Assert.Equal(count, actual.Count);
        Assert.All(actual, item => Assert.Equal(host == "[::1]" ? "[::1]" : host, item.Target));
        Assert.Equal(host == "localhost" ? new[] { "127.0.0.1", "[::1]" } : new[] { host }, actual.Select(item => item.Bind));
        Assert.Equal(port, opened.Port); Assert.True(opened.Pid > 0); Assert.Equal(1, process!.Starts);
        Assert.False(opened.Lost.IsCompleted);
    }

    [Fact]
    public async Task An_occupied_callback_port_starts_nothing()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; var starts = 0;
        var error = await Assert.ThrowsAsync<CallbackPortInUse>(() => LoopbackForward.OpenAsync("inert-profile", "127.0.0.1", port,
            TextWriter.Null, default, (_, _) => { starts++; throw new InvalidOperationException(); }));
        Assert.Equal(port, error.Port); Assert.Equal(0, starts);
    }

    [Fact]
    public async Task A_single_run_exit_completes_loss_and_final_join_without_a_restart()
    {
        RemoteTunnel? process = null;
        await using var forward = await LoopbackForward.OpenAsync("inert-profile", "127.0.0.1", FreePort(), TextWriter.Null, default,
            (forwards, port) => process = Listener(forwards, port));
        System.Diagnostics.Process.GetProcessById(forward.Pid).Kill(entireProcessTree: true);
        await forward.Lost.WaitAsync(TimeSpan.FromSeconds(10));
        await forward.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, process!.Starts); Assert.Equal(0, process.Pid);
    }

    [Fact]
    public async Task Disposal_joins_only_the_exact_forward_and_does_not_report_loss()
    {
        await using var other = await LoopbackForward.OpenAsync("other-profile", "127.0.0.1", FreePort(), TextWriter.Null, default,
            (f, p) => Listener(f, p));
        var owned = await LoopbackForward.OpenAsync("inert-profile", "127.0.0.1", FreePort(), TextWriter.Null, default,
            (f, p) => Listener(f, p));
        await owned.DisposeAsync(); await owned.Completion;
        Assert.Equal(0, owned.Pid); Assert.False(owned.Lost.IsCompleted);
        Assert.True(other.Pid > 0); Assert.False(other.Completion.IsCompleted);
    }

    [Fact]
    public async Task Cancelling_an_open_joins_its_exact_started_process()
    {
        using var stop = new CancellationTokenSource(); RemoteTunnel? process = null;
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var open = LoopbackForward.OpenAsync("inert-profile", "127.0.0.1", FreePort(), TextWriter.Null, stop.Token, (_, _) =>
        {
            process = RemoteTunnel.Wrapped(["python3", "-c", "import select; select.select([],[],[])"], TextWriter.Null, restart: false);
            starting.TrySetResult(); return process;
        });
        await starting.Task; await process!.ProcessStarted.WaitAsync(TimeSpan.FromSeconds(10)); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        await process!.Completion; Assert.Equal(0, process.Pid); Assert.Equal(1, process.Starts);
    }

    [Theory]
    [InlineData("example.invalid", 1455)]
    [InlineData("127.0.0.2", 1455)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", 65536)]
    public async Task Noncanonical_or_invalid_callbacks_never_start_a_process(string host, int port)
    {
        var starts = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => LoopbackForward.OpenAsync("inert-profile", host, port, TextWriter.Null, default,
            (_, _) => { starts++; throw new InvalidOperationException(); }));
        Assert.Equal(0, starts);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal static RemoteTunnel Listener(IReadOnlyList<(string Bind, string Target)> forwards, int port)
    {
        // An inert local process supplies actual bind/readiness/exit events; it never invokes ssh or a provider.
        var binds = string.Join(',', forwards.Select(item => $"'{item.Bind.Trim('[', ']')}'"));
        var script = "import socket,sys,select\nsockets=[]\n" +
            $"for host in [{binds}]:\n s=socket.socket(socket.AF_INET6 if ':' in host else socket.AF_INET,socket.SOCK_STREAM)\n s.bind((host,{port}))\n s.listen()\n sockets.append(s)\n" +
            "print('debug1: Entering interactive session.',file=sys.stderr,flush=True)\n" +
            "while True:\n ready,_,_=select.select(sockets,[],[])\n for s in ready:\n  c,_=s.accept(); c.close()\n";
        return RemoteTunnel.Wrapped(["python3", "-c", script], TextWriter.Null, restart: false);
    }
}
