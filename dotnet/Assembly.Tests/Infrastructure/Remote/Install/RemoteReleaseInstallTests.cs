using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Remote.Install;

namespace Assembly.Tests.Infrastructure.Remote;

/// <summary>
/// The whole install against a local stand-in for GitHub and a throwaway home. The package is the real packager's output
/// and install.sh, agents-file.sh and the download script all really run; only systemctl, ss and the network stand in.
/// </summary>
public sealed partial class RemoteReleaseInstallTests : IDisposable
{
    private const string Version = "1.2.3";
    private const string Archive = "assembly-remote-backend-1.2.3-x86_64-unknown-linux-gnu.tar.gz";
    private const string Base = "https://github.com/jcoble/Assembly/releases/download/assembly-v1.2.3/";
    private static readonly SavedMachine Machine = new("box", "fixture-host");

    private readonly string _root = Directory.CreateTempSubdirectory("remote-release-test-").FullName;
    private ReleaseSigner? LastSigner;
    private string Home => Path.Combine(_root, "home");
    private string Github => Path.Combine(_root, "github");
    private string Bin => Path.Combine(Home, ".local/bin");
    private string Installed => Path.Combine(Home, "assembly-host");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class LocalShell(string home, string github, string pythonPath, string bin) : RemoteReleaseShell
    {
        internal readonly List<string> Targets = [];
        internal string SearchPath = $"{bin}:/usr/bin:/bin";
        internal override Task<string> RunAsync(string target, string command, Action<string>? line, CancellationToken ct)
        {
            lock (Targets) Targets.Add(target);
            return RunProcessAsync(new AgentCommand("sh", ["-c", "cd \"$HOME\"; " + command], home), new Dictionary<string, string>
            {
                ["HOME"] = home,
                ["GITHUB_FIXTURE"] = github,
                ["PYTHONPATH"] = pythonPath,
                ["PATH"] = SearchPath
            }, line, ct);
        }
        internal override Task UploadAsync(string target, string localPath, string remotePath, CancellationToken ct) =>
            RunAsync(target, $"cp {RemoteReleaseInstall.Quote(localPath)} {RemoteReleaseInstall.Quote(remotePath)}", null, ct);
    }

    private static string Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "scripts/remote-backend-release.ts"))) return directory.FullName;
        throw new InvalidOperationException("no repository above the test output");
    }

    private static string Run(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, file + " failed: " + errors.Result);
        return output.Result.Trim();
    }

    private void Script(string name, string text)
    {
        File.WriteAllText(Path.Combine(Bin, name), text);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(Bin, name), (UnixFileMode)0b111_101_101);
    }

    private (RemoteReleaseInstall Install, LocalShell Shell, RemoteReleaseFeedTests.Handler Handler, string Sha) Fixture()
    {
        Directory.CreateDirectory(Bin);
        Directory.CreateDirectory(Github);
        Directory.CreateDirectory(Path.Combine(_root, "python"));
        // The remote's Python reaches "GitHub" only through this: it logs each URL and serves the fixture's file.
        File.WriteAllText(Path.Combine(_root, "python/sitecustomize.py"), """
import os, urllib.request
class Source:
    def __init__(self, url):
        self.url = url
        self.file = open(os.path.join(os.environ['GITHUB_FIXTURE'], os.path.basename(url)), 'rb')
        self.headers = {'Content-Length': str(os.path.getsize(self.file.name))}
    def __enter__(self): return self
    def __exit__(self, *args): self.file.close()
    def geturl(self): return self.url
    def read(self, count): return self.file.read(count)
def fake(request, timeout):
    with open(os.path.join(os.environ['GITHUB_FIXTURE'], 'fetched'), 'a') as log:
        log.write(request.full_url + '\n')
    return Source(request.full_url)
urllib.request.urlopen = fake
""");
        Script("systemctl", """
#!/bin/sh
echo "$*" >>"$HOME/systemctl.log"
case "$*" in
  *restart*) : >"$HOME/assembly-host/serve/assembly.db"; exit "${RESTART_FAILS:-0}" ;;
  *is-active*--quiet*|*--quiet*is-active*) exit 0 ;;
  *is-active*) echo active ;;
  *ControlGroup*) echo "/../../..$HOME/cgroup" ;;
  *MainPID*) echo 1 ;;
esac
exit 0
""");
        // The control group is read at /sys/fs/cgroup + the service's group: that path climbs back out to this folder.
        Directory.CreateDirectory(Path.Combine(Home, "cgroup"));
        File.WriteAllText(Path.Combine(Home, "cgroup/cgroup.procs"), "1\n");
        // Only the host this install started is listening, and it's the service's own process.
        Script("ss", "#!/bin/sh\n[ -e \"$HOME/assembly-host/serve/assembly.db\" ] || exit 0\nfor asked; do :; done\nprintf 'LISTEN 0 128 127.0.0.1:%s 0.0.0.0:* users:((\"assembly\",pid=1,fd=5))\\n' \"${asked##*:}\"\n");
        // agents-file.sh reads the adapter release the desktop named through curl: only its two assets.
        Script("curl", """
#!/bin/sh
out= url=
while [ $# -gt 0 ]; do
  case "$1" in
    -o) out=$2; shift 2 ;;
    -H|--config|--connect-timeout|--max-time) shift 2 ;;
    -*) shift ;;
    *) url=$1; shift ;;
  esac
done
echo "$url" >>"$GITHUB_FIXTURE/fetched"
cp "$GITHUB_FIXTURE/$(basename "$url")" "$out"
""");
        File.CreateSymbolicLink(Path.Combine(Bin, "node"), Run("sh", "-c", "command -v node"));
        AdapterRelease();

        var server = Path.Combine(_root, "server");
        File.WriteAllText(server, "server-bytes");
        var repository = Repository();
        Run("node", Path.Combine(repository, "scripts/remote-backend-release.ts"), Version, server, Github);
        var record = File.ReadAllBytes(Path.Combine(Github, Archive + ".download.json"));
        var signer = new ReleaseSigner();
        LastSigner = signer;
        var signature = signer.Sign(record);
        var adapterManifest = File.ReadAllBytes(Path.Combine(Github, RemoteReleaseFeedTests.AdapterManifest));
        var adapterSignature = signer.Sign(adapterManifest);
        var handler = new RemoteReleaseFeedTests.Handler(new()
        {
            ["https://api.github.com/repos/jcoble/Assembly/releases?per_page=100"] =
                RemoteReleaseFeedTests.Listing(new FileInfo(Path.Combine(Github, Archive)).Length, record.Length, signature.Length, adapterManifestBytes: adapterManifest.Length, adapterSignatureBytes: adapterSignature.Length),
            [Base + Archive + ".download.json"] = record,
            [Base + Archive + ".download.json.sig"] = signature,
            [RemoteReleaseFeedTests.AdapterBase + RemoteReleaseFeedTests.AdapterManifest] = adapterManifest,
            [RemoteReleaseFeedTests.AdapterBase + RemoteReleaseFeedTests.AdapterManifest + ".sig"] = adapterSignature,
        });
        var shell = new LocalShell(Home, Github, Path.Combine(_root, "python"), Bin);
        var install = new RemoteReleaseInstall(new RemoteReleaseFeed(new HttpClient(handler), signer.PublicKey), shell, TimeProvider.System);
        return (install, shell, handler, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(server))));
    }

    private void AdapterRelease()
    {
        var bundle = Path.Combine(_root, "adapter-bundle");
        Directory.CreateDirectory(bundle);
        void Write(string relative, string text)
        {
            var path = Path.Combine(bundle, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write("package.json", "{}");
        Write("package-lock.json", "{}");
        Write("claude-launcher.ts", "// launcher");
        foreach (var (name, version) in new[] { ("claude-agent-acp", "1.0.0"), ("codex-acp", "2.0.0") })
        {
            Write($"node_modules/@agentclientprotocol/{name}/package.json", JsonSerializer.Serialize(new { version }));
            Write($"node_modules/@agentclientprotocol/{name}/dist/index.js", "// adapter");
        }
        var file = Path.Combine(Github, "assembly-adapters-linux-x64.tar.gz");
        Run("tar", "-czf", file, "-C", bundle, "package.json", "package-lock.json", "claude-launcher.ts", "node_modules");
        object Smoke(string version) => new { protocolVersion = 2, info = new { version }, canSteer = true };
        File.WriteAllText(Path.Combine(Github, "assembly-adapters-linux-x64.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["schemaVersion"] = 1,
            ["target"] = "linux-x64",
            ["versions"] = new Dictionary<string, string> { ["claude-agent-acp"] = "1.0.0", ["codex-acp"] = "2.0.0" },
            ["entrypoints"] = new Dictionary<string, string>
            {
                ["claude-agent-acp"] = "claude-launcher.ts",
                ["codex-acp"] = "node_modules/@agentclientprotocol/codex-acp/dist/index.js",
            },
            ["archive"] = new { file = "assembly-adapters-linux-x64.tar.gz", sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))) },
            ["smoke"] = new Dictionary<string, object> { ["claude-agent-acp"] = Smoke("1.0.0"), ["codex-acp"] = Smoke("2.0.0") },
        }));
    }

    [Fact]
    public async Task The_remote_fetches_the_backend_and_adapters_itself_and_ends_up_running_them()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, shell, handler, serverSha) = Fixture();
        var phases = new List<string>();
        var receipt = await install.InstallLatestAsync(Machine, phases.Add, default);

        Assert.Equal(Version, receipt.Version);
        Assert.Matches("^[0-9a-f]{40}$", receipt.Commit);
        Assert.Equal(serverSha, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Installed, "bin/assembly-remote-server")))));
        // Everything heavy went remote -> GitHub; the desktop read only the listing and the two signed files with their signatures.
        var fetched = File.ReadAllText(Path.Combine(Github, "fetched"));
        Assert.Contains(Base + Archive, fetched, StringComparison.Ordinal);
        Assert.Contains("releases/download/assembly-providers-7-1/assembly-adapters-linux-x64.tar.gz", fetched, StringComparison.Ordinal);
        Assert.Equal(5, handler.Asked.Count);
        Assert.DoesNotContain(handler.Asked, url => url.EndsWith(".tar.gz", StringComparison.Ordinal));
        Assert.All(shell.Targets, target => Assert.Equal("fixture-host", target));

        var unit = File.ReadAllText(Path.Combine(Home, ".config/systemd/user/assembly-host.service"));
        Assert.Contains($"ExecStart={Installed}/bin/assembly-remote-server serve --port 9797 --home {Installed}/serve --agents {Installed}/serve/agents.json --token-file {Home}/.config/assembly-host/env", unit, StringComparison.Ordinal);
        using var agents = JsonDocument.Parse(File.ReadAllText(Path.Combine(Installed, "serve/agents.json")));
        Assert.Contains("assembly-providers-7-1", agents.RootElement[0].GetProperty("args")[0].GetString(), StringComparison.Ordinal);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Installed, "serve/backend-manifest.json")));
        Assert.Equal(Version, manifest.RootElement.GetProperty("version").GetString());
        Assert.Empty(Directory.GetDirectories(Path.Combine(Home, ".cache"), ".assembly-install-*"));
        Assert.Contains("Downloading backend on the remote machine…", phases);
    }

    [Fact]
    public async Task A_changed_archive_stops_before_anything_is_installed_and_the_stage_is_removed()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, _, _) = Fixture();
        var archive = Path.Combine(Github, Archive);
        var bytes = File.ReadAllBytes(archive);
        bytes[100] ^= 1;
        File.WriteAllBytes(archive, bytes);
        var failed = await Assert.ThrowsAsync<CallFailed>(() => install.InstallLatestAsync(Machine, _ => { }, default));
        Assert.Contains("differs from signed digest", failed.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Installed, "bin/assembly-remote-server")));
        Assert.False(File.Exists(Path.Combine(Home, "systemctl.log")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(Home, ".cache"), ".assembly-install-*"));
    }

    [Fact]
    public async Task An_adapter_manifest_changed_after_the_desktop_verified_it_stops_the_install()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, _, _) = Fixture();
        // The desktop's copy stays signed; what the remote then downloads differs. The archive's own hash still matches.
        var manifest = Path.Combine(Github, RemoteReleaseFeedTests.AdapterManifest);
        File.WriteAllText(manifest, File.ReadAllText(manifest) + " ");
        var failed = await Assert.ThrowsAsync<CallFailed>(() => install.InstallLatestAsync(Machine, _ => { }, default));
        Assert.Contains("does not match its signature", failed.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Installed, "bin/assembly-remote-server")));
        Assert.DoesNotContain(" stop ", SystemctlLog, StringComparison.Ordinal);
        Assert.DoesNotContain("restart", SystemctlLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cached_adapter_bundle_with_another_manifest_stops_the_install()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, _, _) = Fixture();
        await install.InstallLatestAsync(Machine, _ => { }, default);
        var cached = Directory.GetFiles(Path.Combine(Installed, "adapters/bundles"), "provider-manifest.json", SearchOption.AllDirectories).Single();
        File.WriteAllText(cached, File.ReadAllText(cached) + " ");
        var failed = await Assert.ThrowsAsync<CallFailed>(() => install.InstallLatestAsync(Machine, _ => { }, default));
        Assert.Contains("does not match its signature", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_tool_is_named_before_any_download()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, shell, _, _) = Fixture();
        File.CreateSymbolicLink(Path.Combine(Bin, "uname"), Run("sh", "-c", "command -v uname"));
        shell.SearchPath = Bin; // Only the stand-ins and node: the first real tool it looks for isn't there.
        var failed = await Assert.ThrowsAsync<CallFailed>(() => install.InstallLatestAsync(Machine, _ => { }, default));
        Assert.Contains("Missing remote install tool: python3", failed.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Github, "fetched")));
    }

    [Theory]
    [InlineData("-oProxyCommand=evil", 9797, ".config/assembly-host/env")]
    [InlineData("box", 0, ".config/assembly-host/env")]
    public async Task Refuses_an_unsafe_saved_machine_before_any_command(string ssh, int port, string tokenFile)
    {
        var shell = new LocalShell(Home, Github, _root, Bin);
        var install = new RemoteReleaseInstall(new RemoteReleaseFeed(new HttpClient(new RemoteReleaseFeedTests.Handler([]))), shell, TimeProvider.System);
        var failed = await Assert.ThrowsAsync<CallFailed>(() => install.InstallLatestAsync(new("box", ssh, port, tokenFile), _ => { }, default));
        Assert.Equal("bad_args", failed.Reason);
        Assert.Empty(shell.Targets);
    }

    // Saved before the new default, by another lane's setup, or by hand. The saved value is left alone: it still connects to what it reaches.
    [Theory]
    [InlineData(".config/assembly-dotnet/env")]
    [InlineData(".config/assembly-" + "dotnet-settings2/env")]
    [InlineData("/home/someone/.config/assembly-" + "dotnet/env")]
    [InlineData(".config/assembly-host/../assembly-" + "dotnet/env")]
    [InlineData("../outside/env")]
    [InlineData("a b; rm -rf /")]
    public async Task A_machine_saved_with_any_other_token_file_gets_no_install_and_no_command_runs(string tokenFile)
    {
        var shell = new LocalShell(Home, Github, _root, Bin);
        var install = new RemoteReleaseInstall(new RemoteReleaseFeed(new HttpClient(new RemoteReleaseFeedTests.Handler([]))), shell, TimeProvider.System);
        var failed = await Assert.ThrowsAsync<CallFailed>(() => install.InstallLatestAsync(new("box", "box-host", 9797, tokenFile), _ => { }, default));
        Assert.Equal("token_file_not_ours", failed.Reason);
        Assert.Contains("Clear the token file in Settings", failed.Message, StringComparison.Ordinal);
        Assert.Empty(shell.Targets);
        var tools = await Assert.ThrowsAsync<CallFailed>(() => install.UpdateToolsAsync(new("box", "box-host", 9797, tokenFile), new(true, []), _ => { }, default));
        Assert.Equal("token_file_not_ours", tools.Reason);
        Assert.Empty(shell.Targets);
    }

    private void Session(int pid) => File.AppendAllText(Path.Combine(Home, "cgroup/cgroup.procs"), pid + "\n");

    private static Process RunningSession()
    {
        var start = new ProcessStartInfo("sh", ["-c", "cat"]) { RedirectStandardInput = true };
        return Process.Start(start)!;
    }

    private string SystemctlLog => File.Exists(Path.Combine(Home, "systemctl.log")) ? File.ReadAllText(Path.Combine(Home, "systemctl.log")) : "";

    [Fact]
    public async Task A_running_session_is_waited_for_and_never_stopped_then_the_install_finishes()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, _, serverSha) = Fixture();
        using var session = RunningSession();
        Session(session.Id);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = install.InstallLatestAsync(Machine, phase =>
        {
            if (phase == RemoteReleaseInstall.WaitingWords) waiting.TrySetResult();
        }, default);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(session.HasExited);
        Assert.DoesNotContain(" stop ", SystemctlLog, StringComparison.Ordinal);
        Assert.DoesNotContain("restart", SystemctlLog, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Installed, "bin/assembly-remote-server")));
        Assert.False(running.IsCompleted);

        session.StandardInput.Close();
        await session.WaitForExitAsync();
        var receipt = await running.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(Version, receipt.Version);
        Assert.Contains("restart", SystemctlLog, StringComparison.Ordinal);
        Assert.Equal(serverSha, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Installed, "bin/assembly-remote-server")))));
    }

    [Fact]
    public async Task Cancelling_the_wait_leaves_the_session_and_the_service_alone()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, _, _) = Fixture();
        using var session = RunningSession();
        Session(session.Id);
        using var stop = new CancellationTokenSource();
        var running = install.InstallLatestAsync(Machine, phase =>
        {
            if (phase == RemoteReleaseInstall.WaitingWords) stop.Cancel();
        }, stop.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.False(session.HasExited);
        Assert.DoesNotContain(" stop ", SystemctlLog, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Installed, "bin/assembly-remote-server")));
        session.StandardInput.Close();
        await session.WaitForExitAsync();
    }

    [Fact]
    public async Task A_session_that_starts_after_the_wait_sends_it_back_to_waiting_and_is_not_stopped()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, _, _) = Fixture();
        using var late = RunningSession();
        // agents-file.sh runs between the wait and the stop; a session starting then is what this stand-in plays.
        var real = Run("sh", "-c", "command -v tar");
        Script("tar", "#!/bin/sh\nprintf '%s\\n' " + late.Id + " >>\"$HOME/cgroup/cgroup.procs\"\nexec " + real + " \"$@\"\n");
        var waited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = install.InstallLatestAsync(Machine, phase =>
        {
            if (phase == RemoteReleaseInstall.WaitingWords) waited.TrySetResult();
        }, default);
        await waited.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.False(late.HasExited);
        Assert.DoesNotContain(" stop ", SystemctlLog, StringComparison.Ordinal);
        late.StandardInput.Close();
        await late.WaitForExitAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Contains("restart", SystemctlLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_service_whose_sessions_cannot_be_seen_is_not_stopped()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (install, _, _, _) = Fixture();
        Directory.Delete(Path.Combine(Home, "cgroup"), recursive: true);
        var failed = await Assert.ThrowsAsync<CallFailed>(() => install.InstallLatestAsync(Machine, _ => { }, default));
        Assert.Contains("nothing was stopped", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(" stop ", SystemctlLog, StringComparison.Ordinal);
    }
}
