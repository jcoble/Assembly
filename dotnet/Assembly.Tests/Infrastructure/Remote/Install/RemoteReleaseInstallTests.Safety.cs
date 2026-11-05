using System.Security.Cryptography;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Remote.Install;

namespace Assembly.Tests.Infrastructure.Remote;

/// <summary>An install starts from whatever's on the machine: nothing, one of its own, or somebody else's, which it never touches.</summary>
public sealed partial class RemoteReleaseInstallTests
{
    private string UnitFile => Path.Combine(Home, ".config/systemd/user/assembly-host.service");

    /// <summary>What this installer leaves behind, with a backend that can be told apart from a new one.</summary>
    private void AlreadyInstalled()
    {
        Directory.CreateDirectory(Path.Combine(Installed, "bin"));
        Directory.CreateDirectory(Path.Combine(Installed, "serve"));
        Directory.CreateDirectory(Path.GetDirectoryName(UnitFile)!);
        var server = Path.Combine(Installed, "bin/assembly-remote-server");
        File.WriteAllText(server, "current-backend");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(server, (UnixFileMode)0b111_101_101);
        File.WriteAllText(Path.Combine(Installed, "serve/backend-manifest.json"), "{\n  \"packageType\": \"assembly-remote-backend\"\n}\n");
        File.WriteAllText(UnitFile, $"[Service]\nExecStart={Installed}/bin/assembly-remote-server serve --port 9797\n");
    }

    /// <summary>Every file's path and contents under a folder, so "nothing changed" is checked rather than assumed.</summary>
    private static string[] Contents(string folder) =>
        [.. Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(file => file + " " + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))))];

    private async Task Refused(Func<Task> install, string reason, string words)
    {
        var before = Contents(Installed);
        var unit = File.Exists(UnitFile) ? File.ReadAllText(UnitFile) : null;
        var failed = await Assert.ThrowsAsync<CallFailed>(install);
        Assert.Equal(reason, failed.Reason);
        Assert.Equal(words, failed.Message);
        Assert.Equal(before, Contents(Installed));
        Assert.Equal(unit, File.Exists(UnitFile) ? File.ReadAllText(UnitFile) : null);
        Assert.DoesNotContain("restart", SystemctlLog, StringComparison.Ordinal);
        Assert.DoesNotContain("stop", SystemctlLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_machine_with_nothing_gets_everything_then_its_own_install_is_recognized_next_time()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, _, _) = Fixture();
        var phases = new List<string>();
        await install.InstallLatestAsync(new("box", "fixture-host", 9798, ".config/mine/token"), phases.Add, default);

        Assert.Contains("Checking what's on the remote machine…", phases);
        Assert.Contains($"--port 9798 --home {Installed}/serve --agents {Installed}/serve/agents.json --token-file {Home}/.config/mine/token", File.ReadAllText(UnitFile), StringComparison.Ordinal);
        // The host makes the token itself on first start, in a folder only this account can read.
        if (!OperatingSystem.IsWindows()) Assert.Equal((UnixFileMode)0b111_000_000, File.GetUnixFileMode(Path.Combine(Home, ".config/mine")));

        await install.InstallLatestAsync(new("box", "fixture-host", 9798, ".config/mine/token"), _ => { }, default);
        await install.UpdateToolsAsync(new("box", "fixture-host", 9798, ".config/mine/token"), new(true, []), _ => { }, default);
    }

    [Fact]
    public async Task A_folder_this_installer_did_not_make_stops_the_install_by_name_with_nothing_changed()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, _, _) = Fixture();
        Directory.CreateDirectory(Path.Combine(Installed, "serve"));
        File.WriteAllText(Path.Combine(Installed, "serve/assembly.db"), "someone's data");
        File.WriteAllText(Path.Combine(Installed, "notes"), "someone's notes");
        File.Delete(Path.Combine(Bin, "ss"));
        Script("ss", "#!/bin/sh\nexit 0\n");
        await Refused(() => install.InstallLatestAsync(Machine, _ => { }, default), "install_unrecognized",
            "The folder ~/assembly-host on box isn't an install this app made, so nothing was changed. Assembly won't write there: move it aside yourself if it's not needed.");
    }

    [Fact]
    public async Task A_service_this_installer_did_not_make_stops_the_install_by_name_with_nothing_changed()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, _, _) = Fixture();
        Directory.CreateDirectory(Installed);
        Directory.CreateDirectory(Path.GetDirectoryName(UnitFile)!);
        File.WriteAllText(UnitFile, "[Service]\nExecStart=/opt/someone-else/server\n");
        await Refused(() => install.InstallLatestAsync(Machine, _ => { }, default), "install_unrecognized",
            "The service assembly-host.service on box isn't an install this app made, so nothing was changed. Assembly won't write there: move it aside yourself if it's not needed.");
    }

    [Theory]
    [InlineData("users:((\"other-thing\",pid=2147483646,fd=5))", "other-thing")]
    [InlineData("", "a program this account can't see")]
    public async Task A_port_something_else_holds_stops_the_install_and_says_what_holds_it(string users, string holder)
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, _, _) = Fixture();
        Directory.CreateDirectory(Installed);
        File.Delete(Path.Combine(Bin, "ss"));
        Script("ss", $"#!/bin/sh\nprintf '%s\\n' 'LISTEN 0 128 127.0.0.1:9797 0.0.0.0:* {users}'\n");
        await Refused(() => install.InstallLatestAsync(Machine, _ => { }, default), "port_taken",
            $"Port 9797 on box is in use by {holder}, so nothing was changed. Change the port in Settings, or free it there.");
    }

    [Fact]
    public async Task Tools_are_only_changed_beside_a_backend_this_installer_made()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, _, _) = Fixture();
        Directory.CreateDirectory(Installed);
        await Refused(() => install.UpdateToolsAsync(Machine, new(true, []), _ => { }, default), "install_unrecognized",
            "The backend on box isn't one this app installed, so its adapters and CLIs weren't changed.");
    }

    [Fact]
    public async Task Development_installs_the_same_way_from_this_checkouts_package_with_nothing_downloaded()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (_, shell, _, serverSha) = Fixture();
        // The tool script is the real one's seam: a stand-in node says what it was asked and leaves a real agents file.
        File.Delete(Path.Combine(Bin, "node"));
        Script("node", "#!/bin/sh\necho \"$*\" >>\"$HOME/node.log\"\nprintf '[]' >\"$HOME/assembly-host/serve/agents.json\"\n");
        var built = 0;
        var development = new CheckoutPackages(shell, _ => { built++; return Task.FromResult(Path.Combine(Github, Archive)); });
        var feed = new RemoteReleaseFeed(new HttpClient(new RemoteReleaseFeedTests.Handler([])), LastSigner!.PublicKey);
        var install = new RemoteReleaseInstall(feed, shell, TimeProvider.System, packages: development);
        var phases = new List<string>();

        var receipt = await install.InstallLatestAsync(Machine, phases.Add, default);

        Assert.Equal(Version, receipt.Version);
        Assert.Equal(1, built);
        Assert.Equal(serverSha, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Installed, "bin/assembly-remote-server")))));
        Assert.Contains("payload/scripts/dev-remote-tools.ts", File.ReadAllText(Path.Combine(Home, "node.log")), StringComparison.Ordinal);
        Assert.Contains("Building this checkout for the remote machine…", phases);
        Assert.Contains("Sending the backend to the remote machine…", phases);
        Assert.Contains("Installing adapters and CLIs on the remote machine…", phases);
        Assert.False(File.Exists(Path.Combine(Github, "fetched")));
    }
}
