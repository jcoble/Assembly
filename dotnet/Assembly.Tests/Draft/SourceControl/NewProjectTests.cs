using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
using Assembly.Application.Features.Projects;
using Assembly.Application.Features.Chat.SourceControl.PullRequests;
using Assembly.Infrastructure.Chat.SourceControl;
using Assembly.Infrastructure.Git;
using Assembly.Infrastructure.Projects;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class NewProjectTests
{
    [Theory]
    [InlineData(false, "empty")]
    [InlineData(false, "git")]
    [InlineData(false, "clone")]
    [InlineData(true, "empty")]
    [InlineData(true, "git")]
    [InlineData(true, "clone")]
    public async Task Fixed_create_flow_prepares_only_the_selected_machine_then_registers_once(bool remote, string kind)
    {
        await using var h = await Harness.Start();
        var source = kind == "clone" ? await h.CloneSource() : null;
        var request = new NewProjectCreate(remote ? "workbox-test" : "local", remote ? h.RemoteParent : h.LocalParent, "new ü folder", kind, source);
        using var client = await h.Open();
        var instance = (await h.Call(client, 1, "operation_host", new { action = "createProject" })).GetProperty("result").GetProperty("instanceId").GetString()!;
        var id = Guid.NewGuid();
        await h.Subscribe(client, id);
        var args = new GitOperationStart(instance, id, null, "createProject", JsonSerializer.SerializeToElement(request, ProjectsJson.Default.NewProjectCreate));
        Assert.Equal("result", (await h.Call(client, 2, "start", args)).GetProperty("t").GetString());
        var status = await h.Terminal(client);
        Assert.Equal("succeeded", status.GetProperty("state").GetString());
        var result = status.GetProperty("result").Deserialize(ProjectsJson.Default.NewProjectResult)!;
        var destination = Path.Combine(request.ParentPath, request.Name);
        Assert.Equal(destination, result.Inspection.RootPath); Assert.Equal(kind != "empty", result.Inspection.IsGit);
        Assert.Equal(request.Machine, result.Project.Machine); Assert.False(result.AlreadyExisted);
        Assert.True(Directory.Exists(destination));
        Assert.False(Directory.Exists(Path.Combine(remote ? h.LocalParent : h.RemoteParent, request.Name)));
        if (kind == "clone") Assert.Equal("full clone body ü", await File.ReadAllTextAsync(Path.Combine(destination, "body.txt")));
        Assert.Equal(1, h.Hosts.Mac.Fixture.Count("projects")); Assert.Equal(1, h.Store.Revision(default));
        await h.Call(client, 3, "start", args);
        Assert.Equal(1, h.Hosts.Mac.Fixture.Count("projects")); Assert.Equal(1, h.Store.Revision(default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repository_parent_refusal_precedes_all_mutation_and_registration(bool remote)
    {
        await using var h = await Harness.Start();
        var parent = remote ? h.RemoteParent : h.LocalParent;
        await new GitWriter(new GitCli()).InitAsync(parent, default);
        using var client = await h.Open(); var id = Guid.NewGuid(); await h.Subscribe(client, id);
        await h.Call(client, 1, "start", new GitOperationStart(h.LocalOperations.InstanceId, id, null, "createProject",
            JsonSerializer.SerializeToElement(new NewProjectCreate(remote ? "workbox-test" : "local", parent, "must-not-exist", "empty", null), ProjectsJson.Default.NewProjectCreate)));
        var status = await h.Terminal(client);
        Assert.Equal("failed", status.GetProperty("state").GetString());
        Assert.False(Directory.Exists(Path.Combine(parent, "must-not-exist")));
        Assert.Equal(0, h.Hosts.Mac.Fixture.Count("projects")); Assert.Equal(0, h.Store.Revision(default));
    }

    [Fact]
    public async Task Sessionless_host_and_missing_operation_require_the_fixed_action_instance_and_correct_host()
    {
        await using var h = await Harness.Start(); using var local = await h.Open();
        Assert.Equal("failed", (await h.Call(local, 1, "operation_host", new { })).GetProperty("code").GetString());
        Assert.Equal("failed", (await h.Call(local, 2, "operation", new { operationId = Guid.NewGuid(), action = "createProject" })).GetProperty("code").GetString());
        var missing = await h.Call(local, 3, "operation", new GitOperationArgs(Guid.NewGuid(), Action: "createProject", InstanceId: h.LocalOperations.InstanceId));
        Assert.Equal("uncertain", missing.GetProperty("result").GetProperty("state").GetString());
        using var remote = await h.Open(true);
        Assert.Equal("failed", (await h.Call(remote, 4, "operation_host", new { action = "createProject" })).GetProperty("code").GetString());
        Assert.Equal(0, h.Hosts.Mac.Fixture.Count("projects"));
    }

    [Fact]
    public async Task Remote_folder_reads_resolve_the_saved_profile_to_its_actual_engine_and_socket()
    {
        await using var h = await Harness.Start();
        var child = Path.Combine(h.RemoteParent, "remote child ü");
        Directory.CreateDirectory(child);
        var folders = new RemoteProjectFolders(h.Hosts.Connections, "workbox-test");
        var listing = await folders.ListAsync(h.RemoteParent, default);
        Assert.Equal(h.RemoteParent, listing.Path);
        Assert.Contains(child, listing.Directories);
        Assert.False(listing.Truncated);
        var inspected = await folders.InspectAsync(child, default);
        Assert.Equal(child, inspected.RootPath);
        Assert.False(inspected.IsGit);
        // A provider's engine identity is not a saved connection name.
        Assert.NotEqual("workbox-test", h.Hosts.Remote.Fixture.Machine);
        await Assert.ThrowsAsync<Assembly.Application.Common.Machines.MachineUnreachable>(() => new RemoteProjectFolders(h.Hosts.Connections,
            h.Hosts.Remote.Fixture.Machine).ListAsync(h.RemoteParent, default));
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal readonly TwoHosts Hosts = new(forwardCommands: true);
        private readonly string _root = Directory.CreateTempSubdirectory("assembly-new-project-").FullName;
        private readonly BackgroundWork _localWork = new(TimeProvider.System, TextWriter.Null);
        private readonly BackgroundWork _remoteWork = new(TimeProvider.System, TextWriter.Null);
        internal GitOperations LocalOperations = null!, RemoteOperations = null!;
        internal ProjectStore Store = null!;
        internal string LocalParent => Path.Combine(_root, "desktop");
        internal string RemoteParent => Path.Combine(_root, "workbox-test");
        private readonly List<JsonElement> _changes = [];
        internal static async Task<Harness> Start(IGit? localGit = null, IGit? remoteGit = null)
        {
            var h = new Harness();
            try
            {
                Directory.CreateDirectory(h.LocalParent); Directory.CreateDirectory(h.RemoteParent);
                localGit ??= new GitCli(); remoteGit ??= new GitCli();
                h.LocalOperations = new(h._localWork, h.Hosts.Mac.Topics, TimeProvider.System, h.Hosts.Mac.Fixture.Machine);
                h.RemoteOperations = new(h._remoteWork, h.Hosts.Remote.Topics, TimeProvider.System, h.Hosts.Remote.Fixture.Machine);
                var localFolders = new LocalProjectFolders(localGit); var remoteFolders = new LocalProjectFolders(remoteGit);
                var localWriter = new GitWriter(localGit); var localWrites = new CheckoutWrites();
                var remoteWriter = new GitWriter(remoteGit); var remoteWrites = new CheckoutWrites();
                Assembly.Application.Common.Calls.ICallRegistry? localCalls = null;
                h.Hosts.Mac.AdditionalFeatures = calls => localCalls = calls;
                h.Hosts.Remote.AdditionalFeatures = calls =>
                {
                    new FoldersFeature(remoteFolders).Register(calls);
                    var gh = new GhCli();
                    var remotePrReader = new PullRequestReader(remoteGit, gh, h.RemoteParent, new GitTextPatch(remoteGit));
                    var prs = new PullRequestWriter(remoteGit, gh, remotePrReader);
                    var sessionOperations = new GitSessionOperations(h.Hosts.Remote.Sessions, remoteGit, prs, remoteWrites, h.RemoteOperations, new(remoteGit, new UnavailableUtility()), remotePrReader, h.Hosts.Remote.Fixture.Db);
                    var routes = new SourceControlFeature(h.Hosts.Remote.Sessions, h.Hosts.Remote.Fixture.Db, h.RemoteOperations, null, null);
                    routes.Register(calls);
                    new SourceControlWrites(h.Hosts.Remote.Sessions, remoteGit, remoteWrites, TimeProvider.System, routes).Register(calls);
                    new GitProjectOperations(h.RemoteOperations, new(remoteFolders, remoteWriter, remoteWrites, h.RemoteOperations), sessionOperations, routes).Register(calls);
                };
                await h.Hosts.Start(); await h.Hosts.StartMirror();
                h.Store = new(h.Hosts.Mac.Fixture.Db, TimeProvider.System);
                var coordinator = new NewProjectCoordinator(h.Store, h.Hosts.Mac.Fixture.Db, localFolders, localWriter, localWrites, h.LocalOperations, h.Hosts.Connections);
                var localGh = new GhCli();
                var localPrReader = new PullRequestReader(localGit, localGh, h.LocalParent, new GitTextPatch(localGit));
                var localPrs = new PullRequestWriter(localGit, localGh, localPrReader);
                var localSessionOperations = new GitSessionOperations(h.Hosts.Mac.Sessions, localGit, localPrs, localWrites, h.LocalOperations, new(localGit, new UnavailableUtility()), localPrReader, h.Hosts.Mac.Fixture.Db);
                var routes = new SourceControlFeature(h.Hosts.Mac.Sessions, h.Hosts.Mac.Fixture.Db, h.LocalOperations, h.Hosts.Mac.Forwarder, h.Hosts.Connections);
                routes.Register(localCalls!);
                new SourceControlWrites(h.Hosts.Mac.Sessions, localGit, localWrites, TimeProvider.System, routes).Register(localCalls!);
                new GitProjectOperations(h.LocalOperations, new(localFolders, localWriter, localWrites, h.LocalOperations), localSessionOperations, routes, coordinator).Register(localCalls!);
                return h;
            }
            catch { await h.DisposeAsync(); throw; }
        }
        internal async Task<string> CloneSource()
        {
            var path = Path.Combine(_root, "-source ü"); Directory.CreateDirectory(path);
            var git = new GitCli(); await new GitWriter(git).InitAsync(path, default);
            await File.WriteAllTextAsync(Path.Combine(path, "body.txt"), "full clone body ü");
            Assert.Equal(0, (await git.RunAsync(path, ["add", "--", "body.txt"], null, null, default)).ExitCode);
            Assert.Equal(0, (await git.RunAsync(path, ["-c", "user.name=Git Test", "-c", "user.email=git@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", "fixture"], null, null, default)).ExitCode);
            return path;
        }
        internal async Task<ClientWebSocket> Open(bool remote = false)
        {
            var socket = new ClientWebSocket();
            var address = (remote ? Hosts.Remote : Hosts.Mac).Address;
            await socket.ConnectAsync(new Uri("ws://" + address.Authority + "/socket"), default);
            await Send(socket, """{"t":"hello","protocol":1,"token":"isolated-token"}"""); await Read(socket);
            return socket;
        }
        internal async Task Subscribe(ClientWebSocket socket, Guid id, string? machine = null)
        {
            await Send(socket, JsonSerializer.Serialize(new { t = "sub", id = 100, topic = "gitOperation", key = machine is null ? LocalOperations.Key(id) : machine + "/" + id.ToString("D") }));
            Assert.Equal("state", (await Read(socket)).GetProperty("t").GetString());
        }
        internal Task Unsubscribe(ClientWebSocket socket) => Send(socket, "{\"t\":\"unsub\",\"id\":100}");
        internal async Task<JsonElement> Call(ClientWebSocket socket, int id, string method, object args)
        {
            var payload = args switch
            {
                GitOperationStart start => JsonSerializer.SerializeToElement(start, GitOperationsJson.Default.GitOperationStart),
                GitOperationArgs operation => JsonSerializer.SerializeToElement(operation, GitOperationsJson.Default.GitOperationArgs),
                _ => JsonSerializer.SerializeToElement(args)
            };
            await Send(socket, JsonSerializer.Serialize(new { t = "call", id, feature = "git", method, args = payload }));
            while (true)
            {
                var frame = await Read(socket);
                if (frame.TryGetProperty("id", out var reply) && reply.GetInt32() == id) return frame;
                if (frame.GetProperty("t").GetString() == "change") _changes.Add(frame.GetProperty("item").Clone());
            }
        }
        internal async Task<JsonElement> Running(ClientWebSocket socket)
        {
            while (true)
            {
                var running = _changes.FirstOrDefault(item => item.GetProperty("state").GetString() == "running");
                if (running.ValueKind != JsonValueKind.Undefined) return running;
                var frame = await Read(socket);
                if (frame.GetProperty("t").GetString() == "change") _changes.Add(frame.GetProperty("item").Clone());
            }
        }
        internal async Task<JsonElement> Terminal(ClientWebSocket socket)
        {
            while (true)
            {
                var terminal = _changes.FirstOrDefault(item => item.GetProperty("state").GetString() is "succeeded" or "failed" or "cancelled" or "uncertain");
                if (terminal.ValueKind != JsonValueKind.Undefined) return terminal;
                var frame = await Read(socket);
                if (frame.GetProperty("t").GetString() == "change") _changes.Add(frame.GetProperty("item").Clone());
            }
        }
        private static async Task Send(ClientWebSocket socket, string text) =>
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, default);
        private static async Task<JsonElement> Read(ClientWebSocket socket)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var bytes = new MemoryStream(); var buffer = new byte[16384]; WebSocketReceiveResult frame;
            do { frame = await socket.ReceiveAsync(buffer, stop.Token); bytes.Write(buffer, 0, frame.Count); }
            while (!frame.EndOfMessage);
            return JsonDocument.Parse(bytes.ToArray()).RootElement.Clone();
        }
        public async ValueTask DisposeAsync()
        {
            if (LocalOperations is not null) await LocalOperations.DisposeAsync();
            if (RemoteOperations is not null) await RemoteOperations.DisposeAsync();
            await _localWork.DisposeAsync(); await _remoteWork.DisposeAsync(); await Hosts.DisposeAsync();
            Directory.Delete(_root, true);
        }
    }
}

internal sealed class UnavailableUtility : Assembly.Application.Common.Agents.IUtilityModel
{
    public Task<Assembly.Application.Common.Agents.UtilityResult> CompleteAsync(string prompt, CancellationToken ct) =>
        Task.FromResult(new Assembly.Application.Common.Agents.UtilityResult(Assembly.Application.Common.Agents.UtilityStatus.Unavailable, null, "fixture_unconfigured"));
}
