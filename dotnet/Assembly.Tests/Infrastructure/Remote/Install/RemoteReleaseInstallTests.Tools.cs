using System.Diagnostics;
using System.Security.Cryptography;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Remote.Install;
using Assembly.Infrastructure.Socket;
using Assembly.Infrastructure.Updates;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Remote;

/// <summary>Updating without replacing a current backend, the real install.sh and agents-file.sh on a throwaway home.</summary>
public sealed partial class RemoteReleaseInstallTests
{
    private string AgentsFile => Path.Combine(Installed, "serve/agents.json");

    // A CLI that says the version in its file, and whose updater moves it to what the test wants.
    private void FakeCli(string name, string before, string after, string answer = "{0} (Claude Code)")
    {
        File.WriteAllText(Path.Combine(Bin, name + ".version"), before);
        var said = string.Format(System.Globalization.CultureInfo.InvariantCulture, answer, "$(cat \"$HOME/.local/bin/" + name + ".version\")");
        Script(name, "#!/bin/sh\ncase \"$1\" in --version) echo \"" + said + "\" ;; update) echo " + after + " >\"$HOME/.local/bin/" + name + ".version\" ;; esac\n");
    }

    [Fact]
    public async Task Adapters_alone_install_and_restart_the_same_backend_without_replacing_it()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, _, _) = Fixture();
        Directory.CreateDirectory(Path.Combine(Installed, "bin"));
        File.WriteAllText(Path.Combine(Installed, "bin/assembly-remote-server"), "current-backend");
        await install.UpdateToolsAsync(Machine, new(true, []), _ => { }, default);

        Assert.Equal("current-backend", File.ReadAllText(Path.Combine(Installed, "bin/assembly-remote-server")));
        Assert.False(File.Exists(Path.Combine(Installed, "serve/backend-manifest.json")));
        Assert.False(File.Exists(Path.Combine(Home, ".config/systemd/user/assembly-dotnet.service")));
        Assert.Contains("restart", SystemctlLog, StringComparison.Ordinal);
        Assert.Contains("claude-launcher.ts", File.ReadAllText(AgentsFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cli_alone_updates_with_no_restart_and_waits_for_a_running_session()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, _, _) = Fixture();
        FakeCli("claude", "1.0.0", "2.0.0");
        Directory.CreateDirectory(Path.GetDirectoryName(AgentsFile)!);
        File.WriteAllText(AgentsFile, "[]");
        using var session = RunningSession();
        Session(session.Id);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = install.UpdateToolsAsync(Machine, new(false, [new("claude", "2.0.0")]), phase =>
        {
            if (phase == RemoteReleaseInstall.WaitingWords) waiting.TrySetResult();
        }, default);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(Bin, "claude.version")));
        session.StandardInput.Close();
        await session.WaitForExitAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("2.0.0", File.ReadAllText(Path.Combine(Bin, "claude.version")).Trim());
        Assert.DoesNotContain("restart", SystemctlLog, StringComparison.Ordinal);
        Assert.DoesNotContain(" stop ", SystemctlLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_update_that_exits_clean_with_the_old_version_still_there_failed()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, _, _) = Fixture();
        FakeCli("claude", "1.0.0", "1.0.0");
        Directory.CreateDirectory(Path.GetDirectoryName(AgentsFile)!);
        File.WriteAllText(AgentsFile, "[]");
        var failed = await Assert.ThrowsAsync<CallFailed>(() => install.UpdateToolsAsync(Machine, new(false, [new("claude", "2.0.0")]), _ => { }, default));
        Assert.Equal("version_readback_mismatch", failed.Reason);
    }

    [Fact]
    public async Task The_cli_updated_is_the_one_the_agents_file_runs()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, _, _) = Fixture();
        FakeCli("claude", "1.0.0", "1.0.0");
        var other = Path.Combine(Bin, "chosen");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "claude"), "#!/bin/sh\ncase \"$1\" in --version) cat \"$0.version\" ;; update) echo '2.0.0 (Claude Code)' >\"$0.version\" ;; esac\n");
        File.SetUnixFileMode(Path.Combine(other, "claude"), (UnixFileMode)0b111_101_101);
        File.WriteAllText(Path.Combine(other, "claude.version"), "1.0.0 (Claude Code)");
        Directory.CreateDirectory(Path.GetDirectoryName(AgentsFile)!);
        File.WriteAllText(AgentsFile, "[{\"id\":\"claude\",\"vendorFamily\":\"claude\",\"cli\":\"" + Path.Combine(other, "claude") + "\"}]");
        await install.UpdateToolsAsync(Machine, new(false, [new("claude", "2.0.0")]), _ => { }, default);
        Assert.Equal("2.0.0 (Claude Code)", File.ReadAllText(Path.Combine(other, "claude.version")).Trim());
    }

    [Fact]
    public void Only_a_known_cli_and_a_stable_version_reach_a_shell()
    {
        Assert.Throws<CallFailed>(() => ReleaseCliUpdate.Command(new("codex", "1.0.0; rm -rf /"), "a"));
        Assert.Throws<CallFailed>(() => ReleaseCliUpdate.Command(new("other", "1.0.0"), "a"));
        Assert.Contains("@openai/codex@1.2.3", ReleaseCliUpdate.Command(new("codex", "1.2.3"), "a"), StringComparison.Ordinal);
    }

    private (LocalReleaseTools Tools, string Agents, string Adapters) LocalFixture(RemoteReleaseFeed feed, RemoteReleaseShell shell, Func<bool>? running = null)
    {
        var data = Path.Combine(Home, "mac");
        Directory.CreateDirectory(data);
        var agents = Path.Combine(data, "agents.json");
        return (new(feed, shell, agents, Path.Combine(data, "adapters"), running ?? (() => false), Path.Combine(Repository(), "dotnet/scripts/agents-file.sh")), agents, Path.Combine(data, "adapters"));
    }

    private RemoteReleaseFeed SignedFeed(out LocalShell shell)
    {
        var (_, fixtureShell, handler, _) = Fixture();
        shell = fixtureShell;
        return new(new HttpClient(handler), LastSigner!.PublicKey);
    }

    [Fact]
    public async Task This_computers_adapters_come_from_the_signed_release_through_the_one_installer()
    {
        if (!OperatingSystem.IsLinux()) return;
        var feed = SignedFeed(out var shell);
        var (tools, agents, adapters) = LocalFixture(feed, shell);
        await tools.UpdateAsync(new(true, []), _ => { }, default);
        var written = File.ReadAllText(agents);
        Assert.Contains(adapters, written, StringComparison.Ordinal);
        var manifest = Directory.GetFiles(Path.Combine(adapters, "bundles"), "provider-manifest.json", SearchOption.AllDirectories).Single();
        Assert.Contains("assembly-providers-7-1", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task This_computers_adapters_refuse_a_manifest_other_than_the_signed_one()
    {
        if (!OperatingSystem.IsLinux()) return;
        var feed = SignedFeed(out var shell);
        var (tools, agents, _) = LocalFixture(feed, shell);
        var manifest = Path.Combine(Github, RemoteReleaseFeedTests.AdapterManifest);
        File.WriteAllText(manifest, File.ReadAllText(manifest) + " ");
        var failed = await Assert.ThrowsAsync<CallFailed>(() => tools.UpdateAsync(new(true, []), _ => { }, default));
        Assert.Contains("does not match its signature", failed.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(agents));
    }

    [Fact]
    public async Task This_computers_clis_wait_for_its_running_session_and_say_so()
    {
        if (!OperatingSystem.IsLinux()) return;
        var feed = SignedFeed(out var shell);
        FakeCli("claude", "1.0.0", "2.0.0");
        var (tools, agents, _) = LocalFixture(feed, shell, () => true);
        File.WriteAllText(agents, "[]");
        var busy = await Assert.ThrowsAsync<CallFailed>(() => tools.UpdateAsync(new(false, [new("claude", "2.0.0")]), _ => { }, default));
        Assert.Contains("A session is running", busy.Message, StringComparison.Ordinal);
        Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(Bin, "claude.version")));
        var (free, _, _) = LocalFixture(feed, shell);
        await free.UpdateAsync(new(false, [new("claude", "2.0.0")]), _ => { }, default);
        Assert.Equal("2.0.0", File.ReadAllText(Path.Combine(Bin, "claude.version")).Trim());
    }

    [Fact]
    public async Task The_published_versions_come_from_the_release_and_its_signed_manifest()
    {
        if (!OperatingSystem.IsLinux()) return;
        var feed = SignedFeed(out _);
        var published = await feed.PublishedAsync(default);
        Assert.Equal("1.0.0", published.Adapters!["claude-agent-acp"]);
        Assert.Equal("2.0.0", published.Adapters["codex-acp"]);
        // This fixture's release has no latest.json, which is unknown rather than an error.
        Assert.Null(published.App);
    }

    private static RemoteReleaseFeed AppFeed(string tag, string? latest)
    {
        var url = $"https://github.com/jcoble/Assembly/releases/download/{tag}/latest.json";
        object[] assets = latest is null ? [] : [new { name = "latest.json", size = latest.Length, browser_download_url = url }];
        var listing = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new object[] { new { tag_name = tag, draft = false, prerelease = false, assets } });
        var files = new Dictionary<string, byte[]> { ["https://api.github.com/repos/jcoble/Assembly/releases?per_page=100"] = listing };
        if (latest is not null) files[url] = System.Text.Encoding.UTF8.GetBytes(latest);
        return new(new HttpClient(new RemoteReleaseFeedTests.Handler(files)));
    }

    [Fact]
    public async Task The_apps_wanted_version_is_the_newest_releases_latest_json_and_anything_else_is_unknown()
    {
        Assert.Equal("3.2.1", (await AppFeed("assembly-v3.2.1", "{\"version\":\"3.2.1\"}").PublishedAsync(default)).App);
        Assert.Null((await AppFeed("assembly-v3.2.1", "{\"version\":\"3.2.0\"}").PublishedAsync(default)).App);
        Assert.Null((await AppFeed("assembly-v3.2.1", "not json").PublishedAsync(default)).App);
        Assert.Null((await AppFeed("assembly-v3.2.1", null).PublishedAsync(default)).App);
        var nothing = await new RemoteReleaseFeed(new HttpClient(new RemoteReleaseFeedTests.Handler([]))).PublishedAsync(default);
        Assert.Equal(new PublishedVersions(null, null, null), nothing);
    }

    [Fact]
    public async Task A_hello_from_an_older_remote_installs_the_real_package_by_itself_and_a_session_holds_it_back()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, handler, serverSha) = Fixture();
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        await using var jobs = new RemoteReleaseJobs(install, work, new TopicSet(), (name, _) => name == "box" ? Machine : null, (_, _) => Task.CompletedTask,
            new RemoteReleaseFeed(new HttpClient(handler), LastSigner!.PublicKey));
        var status = new MachineStatus(new TopicSet(), ["box"]);
        var source = UpdateSources.ForHost(new HttpClient(new RemoteReleaseFeedTests.Handler([])), TimeProvider.System, jobs, "");
        var updates = new MachineUpdates(source, status, new InstalledCalls(new AgentsOnlyLauncher(() => []), TimeProvider.System),
            (_, _) => Task.FromResult<InstalledTools?>(null), null, work, TextWriter.Null);
        using var session = RunningSession();
        Session(session.Id);

        updates.Begin();
        status.Backend("box", new("a", "a", false, "0.0.1", BackendBuild.Version));

        await Until(() => status.Items[1].UpdateNote == "box: " + RemoteReleaseInstall.WaitingWords);
        Assert.False(session.HasExited);
        Assert.False(File.Exists(Path.Combine(Installed, "bin/assembly-remote-server")));
        session.StandardInput.Close();
        await session.WaitForExitAsync();
        await Until(() => File.Exists(Path.Combine(Installed, "bin/assembly-remote-server")) && !status.Items[1].Updating);
        Assert.Equal(serverSha, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Installed, "bin/assembly-remote-server")))));
    }

    [Fact]
    public async Task A_current_or_newer_remote_never_installs_by_itself()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, handler, _) = Fixture();
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        await using var jobs = new RemoteReleaseJobs(install, work, new TopicSet(), (name, _) => name == "box" ? Machine : null, (_, _) => Task.CompletedTask,
            new RemoteReleaseFeed(new HttpClient(handler), LastSigner!.PublicKey));
        var status = new MachineStatus(new TopicSet(), ["box"]);
        var source = UpdateSources.ForHost(new HttpClient(new RemoteReleaseFeedTests.Handler([])), TimeProvider.System, jobs, "");
        var updates = new MachineUpdates(source, status, new InstalledCalls(new AgentsOnlyLauncher(() => []), TimeProvider.System),
            (_, _) => Task.FromResult<InstalledTools?>(null), null, work, TextWriter.Null);
        status.Backend("box", new("a", "a", false, "999.0.0", BackendBuild.Version));
        await updates.CheckAsync("box", default);
        status.Backend("box", new("a", "a", false, BackendBuild.Version, BackendBuild.Version));
        await updates.CheckAsync("box", default);
        Assert.False(status.Items[1].Updating);
        Assert.DoesNotContain(status.Items[1].Updates!, item => item.State == UpdateItem.Behind);
        Assert.False(File.Exists(Path.Combine(Installed, "bin/assembly-remote-server")));
    }

    private static async Task Until(Func<bool> done)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!done()) await Task.Delay(10, deadline.Token);
    }
}
