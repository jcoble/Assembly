using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class SshHostsTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ssh-hosts-test-").FullName;
    private string Ssh => Path.Combine(_home, ".ssh");

    public SshHostsTests() => Directory.CreateDirectory(Path.Combine(Ssh, "conf.d"));

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private void Config(string text) => File.WriteAllText(Path.Combine(Ssh, "config"), text);

    [Fact]
    public void Aliases_come_first_with_their_names_then_known_hosts_others_and_patterns_and_hashes_are_left_out()
    {
        Config("""
            # a comment
            Host box other
              HostName box.example
              User me
            Host *.internal !never ?ingle
              HostName ignored.example
            HOST "quoted"
              hostname=quoted.example
            Host plain
            Match host box
              HostName matched.example
            Include conf.d/*.conf
            Include ~/more.conf missing.conf
            """);
        File.WriteAllText(Path.Combine(Ssh, "conf.d/b.conf"), "Host included\n  HostName included.example\n# Include cycles are read once\nInclude config\n");
        File.WriteAllText(Path.Combine(Ssh, "conf.d/a.conf"), "Host first-included\n");
        File.WriteAllText(Path.Combine(_home, "more.conf"), "Host from-home\n  HostName home.example\n");
        File.WriteAllText(Path.Combine(Ssh, "known_hosts"), """
            box ssh-ed25519 AAAA
            seen.example,10.0.0.9 ssh-ed25519 AAAA
            [ported.example]:2222 ssh-ed25519 AAAA
            |1|hashedsalt=|hashedname= ssh-ed25519 AAAA
            *.wild.example ssh-ed25519 AAAA
            !negated.example ssh-ed25519 AAAA
            @cert-authority marked.example ssh-ed25519 AAAA
            # a comment

            """);

        var hosts = SshHosts.Read(_home);

        Assert.Equal(
        [
            new("box", "box.example"), new("other", "box.example"), new("quoted", "quoted.example"), new("plain", null),
            new("first-included", null), new("included", "included.example"), new("from-home", "home.example"),
            new("seen.example", null), new("10.0.0.9", null), new("ported.example", null), new("marked.example", null),
        ], hosts);
    }

    [Fact]
    public void No_ssh_files_is_an_empty_list()
    {
        Directory.Delete(Ssh, recursive: true);
        Assert.Empty(SshHosts.Read(_home));
    }

    [Fact]
    public async Task What_a_host_means_comes_from_ssh_itself()
    {
        if (!OperatingSystem.IsLinux()) return;
        var config = Path.Combine(Ssh, "config");
        Config("Host box\n  HostName box.example\n  User me\n  Port 2222\nHost *.wild\n  User wildcard\n");

        Assert.Equal(new("box", "me", "box.example", 2222), await SshHosts.TargetAsync("box", config, default));
        // A rule this code never parses: SSH's own pattern matching decides, and an unlisted name works.
        var wild = await SshHosts.TargetAsync("a.wild", config, default);
        Assert.Equal(("wildcard", "a.wild", 22), (wild.User, wild.HostName, wild.Port));
    }

    [Theory]
    [InlineData("-oProxyCommand=touch")]
    [InlineData("a b")]
    [InlineData("")]
    public async Task A_host_that_could_be_an_option_never_reaches_ssh(string host)
    {
        var failed = await Assert.ThrowsAsync<CallFailed>(() => SshHosts.TargetAsync(host, null, default));
        Assert.Equal("That SSH host isn't valid.", failed.Message);
    }
}
