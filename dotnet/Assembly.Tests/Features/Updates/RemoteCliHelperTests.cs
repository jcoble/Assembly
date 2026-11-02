using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Features.Updates;

public sealed class RemoteCliHelperTests
{
    [Fact]
    public async Task Actual_fixed_helper_reads_the_owning_private_capture_with_no_backend_listener()
    {
        using var f = new CliCheckFixture();
        var capture = RemoteCliCaptureTests.Capture(f);
        var file = Path.Combine(f.Home, "private-capture");
        await RemoteCliCapture.SaveAsync(file, capture, default);
        var bytes = await new SshUpdateProcess(TimeProvider.System).ReadAsync(Invocation(f, file), default, default);
        var result = JsonSerializer.Deserialize(bytes, RemoteCliJson.Default.RemoteCliReadResult)!;
        Assert.Equal(capture.InstallationId, result.InstallationId);
        Assert.Equal(f.Version, result.InstalledVersion); Assert.Equal("npm", result.Source);
        Assert.DoesNotContain(f.Home, System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Lifetime_EOF_cancels_and_joins_actual_helper_cli_children()
    {
        using var f = new CliCheckFixture();
        var capture = RemoteCliCaptureTests.Capture(f); var file = Path.Combine(f.Home, "private-capture");
        await RemoteCliCapture.SaveAsync(file, capture, default);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        CliCheckFixture.WriteExecutable(f.Entry, $$"""
            use IO::Socket::INET;
            my $s=IO::Socket::INET->new(PeerAddr=>'127.0.0.1',PeerPort=>{{port}},Proto=>'tcp') or die;
            $s->autoflush(1); print $s qq($$\n); my $line=<$s>;
            """);
        using var cancellation = new CancellationTokenSource();
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var read = new SshUpdateProcess(TimeProvider.System).ReadAsync(Invocation(f, file), cancellation.Token, default);
        using var ready = await listener.AcceptTcpClientAsync(bound.Token);
        using var reader = new StreamReader(ready.GetStream());
        var pid = int.Parse((await reader.ReadLineAsync(bound.Token))!);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(bound.Token));
        try
        {
            using var child = Process.GetProcessById(pid);
            Assert.True(child.HasExited || OperatingSystem.IsLinux() && File.ReadAllText($"/proc/{pid}/stat").Split(' ')[2] == "Z");
        }
        catch (ArgumentException) { }
        catch (FileNotFoundException) { }
    }

    [Fact]
    public async Task Quoted_fixed_invocation_preserves_each_actual_argument_without_shell_interpretation()
    {
        using var f = new CliCheckFixture();
        var captureFile = Path.Combine(f.Home, "capture ' $(touch should-not-exist)");
        var helper = Path.Combine(f.Home, "helper ' executable");
        CliCheckFixture.WriteExecutable(helper, "print join(qq(\\0), @ARGV);");
        var binding = new RemoteCliBinding(new("saved", "workbox-test", 12345), "remote", Guid.NewGuid().ToString("N"), captureFile,
            new(helper, ["managed ' argument"], f.Home), "codex", "provider", "", new string('0', 64));
        var ssh = RemoteCliClient.FixedInvocation(binding);
        var shell = new AgentCommand("/bin/sh", ["-c", ssh.Arguments.Last()], f.Home);
        var bytes = await new SshUpdateProcess(TimeProvider.System).ReadAsync(new(shell), default, default);
        Assert.Equal(new[] { "managed ' argument", "cli-read", "--capture", captureFile }, System.Text.Encoding.UTF8.GetString(bytes).Split('\0'));
        Assert.False(File.Exists(Path.Combine(f.Home, "should-not-exist")));
        Assert.DoesNotContain("12345", ssh.Arguments); // SavedMachine.Port is not the SSH port.
        Assert.DoesNotContain("StrictHostKeyChecking=no", ssh.Arguments);
    }

    [Fact]
    public async Task Partial_initial_request_still_exits_at_the_real_helper_deadline()
    {
        using var f = new CliCheckFixture();
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = f.Home
        };
        foreach (var arg in new[] { typeof(Assembly.Remote.Serve.ServeOptions).Assembly.Location, "cli-read", "--capture", Path.Combine(f.Home, "unused-capture") })
            start.ArgumentList.Add(arg);
        using var helper = Process.Start(start)!;
        try
        {
            var stdout = helper.StandardOutput.ReadToEndAsync(); var stderr = helper.StandardError.ReadToEndAsync();
            await helper.StandardInput.WriteAsync("{\"machineId\":");
            await helper.StandardInput.FlushAsync();
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(27));
            await helper.WaitForExitAsync(bound.Token);
            Assert.Equal(1, helper.ExitCode); Assert.Equal("", await stdout);
            Assert.Equal("CLI check unavailable.", (await stderr).Trim());
        }
        finally
        {
            if (!helper.HasExited) helper.Kill(entireProcessTree: true);
            await helper.WaitForExitAsync();
        }
    }

    private static SshCliReadInvocation Invocation(CliCheckFixture fixture, string captureFile) =>
        new(new AgentCommand("dotnet", [typeof(Assembly.Remote.Serve.ServeOptions).Assembly.Location, "cli-read", "--capture", captureFile], fixture.Home),
            Request: JsonSerializer.SerializeToUtf8Bytes(fixture.Args, UpdatesJson.Default.CliCheckArgs));
}
