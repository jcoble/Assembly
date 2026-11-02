using System.Net;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Updates;
using Assembly.Tests.Features.Chat.Sessions;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Features.Updates;

internal sealed class CliCheckFixture : IDisposable
{
    public string Home { get; } = Directory.CreateTempSubdirectory("assembly-cli-check-").FullName;
    public FakeLauncher Launcher { get; } = new();
    public FakeTimeProvider Time { get; } = new();
    public Handler HttpHandler { get; } = new();
    public HttpClient Http { get; }
    public string Bin => Path.Combine(Home, "bin");
    public string Root => Path.Combine(Home, "global");
    public string Package => Path.Combine(Root, Family == "codex" ? "@openai/codex" : "@anthropic-ai/claude-code");
    public string Entry => Path.Combine(Package, "bin", "cli");
    public string Executable => Path.Combine(Bin, Family);
    public string Family { get; }
    public string Version { get; }
    public CliCheckFixture(string family = "codex", string version = "0.9.0")
    {
        Family = family; Version = version; Http = new(HttpHandler);
        Directory.CreateDirectory(Bin); Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        WriteExecutable(Entry, "die 'wrong args' unless @ARGV == 1 && $ARGV[0] eq '--version';\n" +
            "die 'wrong private account' unless $ENV{'PRIVATE_HOME'} eq $ENV{'CODEX_HOME'};\n" +
            "open(my $f, '>', $ENV{'PROBE_RECORD'}) or die; print $f join('|', @ARGV), qq(\n), $ENV{'PRIVATE_HOME'}; close($f);\n" +
            "print " + Quote(family == "codex" ? "codex-cli " + version + "\n" : version + " (Claude Code)\n") + ";");
        File.CreateSymbolicLink(Executable, Entry);
        WriteExecutable(Path.Combine(Bin, "npm"), "die 'wrong npm args' unless @ARGV == 2 && $ARGV[0] eq 'root' && $ARGV[1] eq '-g'; print $ENV{'FIXTURE_ROOT'}, qq(\n);");
        SetPackage(family == "codex" ? "@openai/codex" : "@anthropic-ai/claude-code", version);
        var raw = Path.Combine(Home, "private-account-canary");
        Launcher.Agents = [new("custom-provider", "Selected provider", "/never/execute/adapter", ["PRIVATE_ARG"],
            new Dictionary<string, string> { ["PATH"] = Bin, ["CODEX_HOME"] = raw, ["CLAUDE_CONFIG_DIR"] = raw,
                ["PRIVATE_HOME"] = raw, ["FIXTURE_ROOT"] = Root, ["PROBE_RECORD"] = Path.Combine(Home, "probe-record") },
            Cli: family, VendorFamily: family)];
        HttpHandler.Version = "0.10.0"; HttpHandler.Package = family == "codex" ? "@openai/codex" : "@anthropic-ai/claude-code";
    }
    public AgentDescriptor Agent => Launcher.Agents.Single();
    public CliCheckArgs Args
    {
        get
        {
            var captured = ConfigTargetIdentity.Capture(Launcher, Agent.Id);
            return new("local-machine", Agent.Id, AccountContext.Of(captured.Descriptor, k => captured.Environment.GetValueOrDefault(k)), captured.TargetId);
        }
    }
    public Task<CliCheckResult> Check(CliCheckArgs? args = null, CancellationToken ct = default, CancellationToken hostStop = default,
        CallPeer? peer = null) => new UpdateCalls(Launcher, "local-machine", Http, Time, hostStop)
            .CheckAsync(args ?? Args, new(ct, "fixture", Peer: peer ?? new OwnerPeer()));
    public async Task<CliInstalled> Probe(CancellationToken ct = default)
    {
        var capture = ConfigTargetIdentity.Capture(Launcher, Agent.Id);
        return await new CliCommand(Time).ProbeAsync(Launcher, capture.Descriptor, capture.Environment, ct);
    }
    public void SetPackage(string name, string version, string bin = "bin/cli") => File.WriteAllText(Path.Combine(Package, "package.json"),
        JsonSerializer.Serialize(new { name, version, bin = new Dictionary<string, string> { [Family] = bin } }));
    public static void WriteExecutable(string path, string body)
    {
        File.WriteAllText(path, "#!/usr/bin/perl\nuse strict; use warnings;\n" + body, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    public void Dispose() { Http.Dispose(); Directory.Delete(Home, true); }
    internal sealed class Handler : HttpMessageHandler
    {
        public string Version { get; set; } = "0.10.0";
        public string Package { get; set; } = "@openai/codex";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Gate { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Entered.TrySetResult(); if (Gate) await Release.Task.WaitAsync(ct);
            return new(Status) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(new { name = Package, version = Version })) };
        }
    }
}
