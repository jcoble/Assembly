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
public sealed class RemoteReleaseInstallTests : IDisposable
{
    private const string Version = "1.2.3";
    private const string Archive = "assembly-remote-backend-1.2.3-x86_64-unknown-linux-gnu.tar.gz";
    private const string Base = "https://github.com/jcoble/Assembly/releases/download/assembly-backend-v1.2.3/";
    private static readonly SavedMachine Machine = new("box", "fixture-host");

    private readonly string _root = Directory.CreateTempSubdirectory("remote-release-test-").FullName;
    private string Home => Path.Combine(_root, "home");
    private string Github => Path.Combine(_root, "github");
    private string Bin => Path.Combine(Home, ".local/bin");
    private string Installed => Path.Combine(Home, "assembly-dotnet");

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
  *restart*) : >"$HOME/assembly-dotnet/serve/assembly.db"; exit "${RESTART_FAILS:-0}" ;;
  *is-active*--quiet*|*--quiet*is-active*) exit 0 ;;
  *is-active*) echo active ;;
esac
exit 0
""");
        Script("ss", "#!/bin/sh\nprintf 'LISTEN 0 128 127.0.0.1:9797 0.0.0.0:*\\n'\n");
        // agents-file.sh and package-adapters.ts read the adapter release through curl: the API listing, then the two assets.
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
case "$url" in
  *api.github.com*) cat "$GITHUB_FIXTURE/adapter-releases.json" ;;
  *) cp "$GITHUB_FIXTURE/$(basename "$url")" "$out" ;;
esac
""");
        File.CreateSymbolicLink(Path.Combine(Bin, "node"), Run("sh", "-c", "command -v node"));
        AdapterRelease();

        var server = Path.Combine(_root, "server");
        File.WriteAllText(server, "server-bytes");
        var repository = Repository();
        Run("node", Path.Combine(repository, "scripts/remote-backend-release.ts"), Version, server, Github);
        var record = File.ReadAllBytes(Path.Combine(Github, Archive + ".download.json"));
        var signer = new ReleaseSigner();
        var signature = signer.Sign(record);
        var handler = new RemoteReleaseFeedTests.Handler(new()
        {
            ["https://api.github.com/repos/jcoble/Assembly/releases?per_page=100"] =
                RemoteReleaseFeedTests.Listing(new FileInfo(Path.Combine(Github, Archive)).Length, record.Length, signature.Length),
            [Base + Archive + ".download.json"] = record,
            [Base + Archive + ".download.json.sig"] = signature,
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
        File.WriteAllText(Path.Combine(Github, "adapter-releases.json"), JsonSerializer.Serialize(new object[]
        {
            new { tag_name = "assembly-providers-7-1", draft = false, prerelease = false, published_at = "2026-11-01T00:00:00Z", assets = new object[]
            { new { name = "assembly-adapters-linux-x64.tar.gz" }, new { name = "assembly-adapters-linux-x64.json" } } },
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
        // Everything heavy went remote -> GitHub; the desktop read only the listing, the record and its signature.
        var fetched = File.ReadAllText(Path.Combine(Github, "fetched"));
        Assert.Contains(Base + Archive, fetched, StringComparison.Ordinal);
        Assert.Contains("releases/download/assembly-providers-7-1/assembly-adapters-linux-x64.tar.gz", fetched, StringComparison.Ordinal);
        Assert.Equal(3, handler.Asked.Count);
        Assert.DoesNotContain(handler.Asked, url => url.EndsWith(".tar.gz", StringComparison.Ordinal));
        Assert.All(shell.Targets, target => Assert.Equal("fixture-host", target));

        var unit = File.ReadAllText(Path.Combine(Home, ".config/systemd/user/assembly-dotnet.service"));
        Assert.Contains($"ExecStart={Installed}/bin/assembly-remote-server serve --port 9797 --home {Installed}/serve --agents {Installed}/serve/agents.json --token-file {Home}/.config/assembly-dotnet/env", unit, StringComparison.Ordinal);
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
    [InlineData("-oProxyCommand=evil", 9797, ".config/assembly-dotnet/env")]
    [InlineData("box", 0, ".config/assembly-dotnet/env")]
    [InlineData("box", 9797, "../outside/env")]
    [InlineData("box", 9797, "a b; rm -rf /")]
    public async Task Refuses_an_unsafe_saved_machine_before_any_command(string ssh, int port, string tokenFile)
    {
        var shell = new LocalShell(Home, Github, _root, Bin);
        var install = new RemoteReleaseInstall(new RemoteReleaseFeed(new HttpClient(new RemoteReleaseFeedTests.Handler([]))), shell, TimeProvider.System);
        var failed = await Assert.ThrowsAsync<CallFailed>(() => install.InstallLatestAsync(new("box", ssh, port, tokenFile), _ => { }, default));
        Assert.Equal("bad_args", failed.Reason);
        Assert.Empty(shell.Targets);
    }
}
