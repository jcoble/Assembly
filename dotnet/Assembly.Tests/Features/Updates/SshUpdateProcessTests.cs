using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Updates;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Features.Updates;

public sealed class SshUpdateProcessTests
{
    [Fact]
    public async Task A_captured_read_returns_private_bytes_and_joins_the_required_supervisor()
    {
        using var f = new Fixture("print qq({\"installedVersion\":\"0.9.0\"});");
        Assert.Equal("{\"installedVersion\":\"0.9.0\"}", Encoding.UTF8.GetString(await f.Read()));
        Assert.Equal(1, f.Supervisor.Joins);
    }

    [Theory]
    [InlineData("print 'x' x 65537;", "output_too_large")]
    [InlineData("print STDERR 'x' x 16385;", "output_too_large")]
    [InlineData("print STDERR 'private-canary'; exit 255;", "ssh_unreachable")]
    [InlineData("print STDERR 'private-canary'; exit 7;", "nonzero_exit")]
    public async Task Output_bounds_and_process_errors_are_sanitized_and_join_the_supervisor(string body, string reason)
    {
        using var f = new Fixture(body);
        var error = await Assert.ThrowsAsync<CallFailed>(f.Read);
        Assert.Equal(reason, error.Reason); Assert.DoesNotContain("private-canary", error.Message);
        Assert.Equal(1, f.Supervisor.Joins);
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("host")]
    [InlineData("deadline")]
    [InlineData("exited-leader")]
    public async Task Cancellation_or_deadline_joins_the_owned_local_tree_and_calls_remote_cleanup(string cause)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var f = new Fixture($$"""
            use IO::Socket::INET;
            my $s=IO::Socket::INET->new(PeerAddr=>'127.0.0.1',PeerPort=>{{port}},Proto=>'tcp') or die;
            $s->autoflush(1); my $child=fork(); die unless defined $child;
            if ($child==0) { close($s); while(1) { sleep 3600; } }
            print $s qq($$ $child\n); {{(cause == "exited-leader" ? "exit 0;" : "my $line=<$s>;")}}
            """);
        using var caller = new CancellationTokenSource(); using var host = new CancellationTokenSource();
        var read = f.Read(caller.Token, host.Token);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var ready = await listener.AcceptTcpClientAsync(bound.Token);
        using var reader = new StreamReader(ready.GetStream());
        var ids = (await reader.ReadLineAsync(bound.Token))!.Split(' ').Select(int.Parse).ToArray();
        if (cause == "caller") caller.Cancel(); else if (cause == "host") host.Cancel(); else f.Time.Advance(TimeSpan.FromSeconds(20));
        if (cause is "deadline" or "exited-leader") Assert.Equal("timeout", (await Assert.ThrowsAsync<CallFailed>(() => read.WaitAsync(bound.Token))).Reason);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(bound.Token));
        Assert.Equal(1, f.Supervisor.Joins);
        foreach (var id in ids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                Assert.True(process.HasExited || OperatingSystem.IsLinux() && File.ReadAllText($"/proc/{id}/stat").Split(' ')[2] == "Z", "Owned local child still running");
            }
            catch (ArgumentException) { }
            catch (FileNotFoundException) { }
        }
    }

    [Fact]
    public async Task Remote_cleanup_releases_inherited_streams_before_the_final_drain_join()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var f = new Fixture($$"""
            use IO::Socket::INET; use POSIX qw(setsid);
            my $s=IO::Socket::INET->new(PeerAddr=>'127.0.0.1',PeerPort=>{{port}},Proto=>'tcp') or die;
            $s->autoflush(1); my $child=fork(); die unless defined $child;
            if ($child==0) { setsid() or die; print $s qq($$\n); close($s); while(1) { sleep 3600; } }
            close($s); exit 0;
            """);
        var remoteChild = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Supervisor.Join = async () =>
        {
            using var child = Process.GetProcessById(await remoteChild.Task);
            child.Kill();
        };
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var read = f.Read();
        using var ready = await listener.AcceptTcpClientAsync(bound.Token);
        using var reader = new StreamReader(ready.GetStream());
        var pid = int.Parse((await reader.ReadLineAsync(bound.Token))!);
        remoteChild.SetResult(pid);
        Assert.Null(await reader.ReadLineAsync(bound.Token));
        f.Time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal("timeout", (await Assert.ThrowsAsync<CallFailed>(() => read.WaitAsync(bound.Token))).Reason);
        Assert.Equal(1, f.Supervisor.Joins);
    }

    // A real local process fixture. This supervisor records the required callback; it does not simulate or
    // establish the still-unbound remote helper's own child cleanup.
    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Directory.CreateTempSubdirectory("ssh-cli-read-").FullName;
        private readonly string _executable;
        internal FakeTimeProvider Time { get; } = new();
        internal Supervisor Supervisor { get; } = new();
        internal Fixture(string body)
        {
            _executable = Path.Combine(_folder, "ssh"); CliCheckFixture.WriteExecutable(_executable, body);
        }
        internal Task<byte[]> Read() => Read(default, default);
        internal Task<byte[]> Read(CancellationToken caller, CancellationToken host) =>
            new SshUpdateProcess(Time).ReadAsync(new(new AgentCommand(_executable, [], _folder), Supervisor), caller, host);
        public void Dispose() => Directory.Delete(_folder, true);
    }
    private sealed class Supervisor : IAsyncDisposable
    {
        internal int Joins { get; private set; }
        internal Func<Task>? Join { get; set; }
        public async ValueTask DisposeAsync() { Joins++; if (Join is not null) await Join(); }
    }
}
