using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Features.Updates;

public sealed class CliCommandTests
{
    [Theory]
    [InlineData("codex", "0.9.0")]
    [InlineData("claude", "2.1.0")]
    public async Task Actual_version_and_global_package_link_qualify_same_executable_without_adapter_launch(string family, string version)
    {
        using var f = new CliCheckFixture(family, version);
        var installed = await f.Probe();
        Assert.Equal(version, installed.Version); Assert.Equal("npm", installed.Manager); Assert.Equal("npm", installed.Source);
        Assert.Equal(0, f.Launcher.Launches);
        Assert.Equal("--version\n" + f.Agent.Env!["PRIVATE_HOME"], File.ReadAllText(Path.Combine(f.Home, "probe-record")));
    }

    [Theory]
    [InlineData("wrong-package")]
    [InlineData("wrong-version")]
    [InlineData("wrong-link")]
    [InlineData("unsafe-bin")]
    [InlineData("missing-npm")]
    public async Task Matching_version_or_npm_location_alone_never_establishes_manager(string change)
    {
        using var f = new CliCheckFixture();
        switch (change)
        {
            case "wrong-package": f.SetPackage("@someone/other", f.Version); break;
            case "wrong-version": f.SetPackage("@openai/codex", "99.0.0"); break;
            case "unsafe-bin": f.SetPackage("@openai/codex", f.Version, "../outside"); break;
            case "missing-npm": File.Delete(Path.Combine(f.Bin, "npm")); break;
            default:
                File.Delete(f.Executable); File.Copy(f.Entry, f.Executable); break;
        }
        var result = await f.Check();
        Assert.Equal(f.Version, result.InstalledVersion); Assert.Equal("unknown", result.Manager);
        Assert.Equal("unsupported", result.Comparison); Assert.Equal("unsupported_installation", result.Reason);
    }

    [Theory]
    [InlineData("relative/cli")]
    [InlineData("codex --help")]
    [InlineData("")]
    public async Task Relative_or_argument_configuration_is_never_executed(string cli)
    {
        using var f = new CliCheckFixture(); f.Launcher.Agents = [f.Agent with { Cli = cli }];
        var result = await f.Check();
        Assert.Equal("cli_missing", result.Reason); Assert.Null(result.InstalledVersion);
        Assert.False(File.Exists(Path.Combine(f.Home, "probe-record")));
    }

    [Fact]
    public async Task Empty_relative_PATH_entries_do_not_resolve_even_when_working_directory_has_the_CLI()
    {
        using var f = new CliCheckFixture();
        var environment = f.Agent.Env!.ToDictionary(p => p.Key, p => p.Value); environment["PATH"] = ":.:relative:";
        f.Launcher.Agents = [f.Agent with { Env = environment }];
        Assert.Equal("cli_missing", (await f.Check()).Reason);
    }

    [Theory]
    [InlineData("print qq(codex-cli 0.9.0\\n); exit 4;", "nonzero_exit")]
    [InlineData("print qq(private_unexamined_output);", "unrecognized_version")]
    [InlineData("print 'x' x 65537;", "output_too_large")]
    [InlineData("print STDERR 'x' x 16385; print qq(codex-cli 0.9.0\\n);", "output_too_large")]
    public async Task Actual_process_failures_are_distinct_and_output_never_reaches_result(string body, string reason)
    {
        using var f = new CliCheckFixture(); CliCheckFixture.WriteExecutable(f.Entry, body);
        var result = await f.Check();
        Assert.Equal(reason, result.Reason); Assert.Equal("unverified", result.Comparison);
        Assert.DoesNotContain("private_unexamined_output", System.Text.Json.JsonSerializer.Serialize(result, UpdatesJson.Default.CliCheckResult));
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("host")]
    [InlineData("deadline")]
    public async Task Cancellation_deadline_and_host_stop_join_the_actual_process_group(string cause)
    {
        using var f = new CliCheckFixture();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        CliCheckFixture.WriteExecutable(f.Entry, $$"""
            use IO::Socket::INET;
            my $s = IO::Socket::INET->new(PeerAddr=>'127.0.0.1',PeerPort=>{{port}},Proto=>'tcp') or die;
            $s->autoflush(1); my $child=fork(); die unless defined $child;
            if ($child==0) { close($s); while(1) { sleep 3600; } }
            print $s qq($$ $child\n); my $line=<$s>;
            """);
        using var caller = new CancellationTokenSource(); using var host = new CancellationTokenSource();
        var checking = f.Check(ct: caller.Token, hostStop: host.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var ready = await listener.AcceptTcpClientAsync(timeout.Token);
        using var reader = new StreamReader(ready.GetStream());
        var ids = (await reader.ReadLineAsync(timeout.Token))!.Split(' ').Select(int.Parse).ToArray();
        if (cause == "caller") caller.Cancel(); else if (cause == "host") host.Cancel(); else f.Time.Advance(TimeSpan.FromSeconds(20));
        if (cause == "deadline") Assert.Equal("timeout", (await checking.WaitAsync(timeout.Token)).Reason);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checking.WaitAsync(timeout.Token));
        foreach (var id in ids)
        {
            try
            {
                using var child = Process.GetProcessById(id);
                Assert.True(child.HasExited || OperatingSystem.IsLinux() && File.ReadAllText($"/proc/{id}/stat").Split(' ')[2] == "Z", "Owned child still running");
            }
            catch (ArgumentException) { }
            catch (FileNotFoundException) { }
        }
    }
}
