using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
using Assembly.Application.Features.Projects;
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
                    new GitProjectOperations(h.RemoteOperations, new(remoteFolders, remoteWriter, remoteWrites, h.RemoteOperations)).Register(calls);
                };
                await h.Hosts.Start(); await h.Hosts.StartMirror();
                h.Store = new(h.Hosts.Mac.Fixture.Db, TimeProvider.System);
                var coordinator = new NewProjectCoordinator(h.Store, h.Hosts.Mac.Fixture.Db, localFolders, localWriter, localWrites, h.LocalOperations, h.Hosts.Connections);
                new GitProjectOperations(h.LocalOperations, new(localFolders, localWriter, localWrites, h.LocalOperations), coordinator).Register(localCalls!);
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
        internal async Task Subscribe(ClientWebSocket socket, Guid id)
        {
            await Send(socket, JsonSerializer.Serialize(new { t = "sub", id = 100, topic = "gitOperation", key = LocalOperations.Key(id) }));
            Assert.Equal("state", (await Read(socket)).GetProperty("t").GetString());
        }
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
