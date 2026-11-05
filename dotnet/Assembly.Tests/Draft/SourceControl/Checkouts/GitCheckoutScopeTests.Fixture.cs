using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Git;
using Assembly.Application.Common.Projects;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.SourceControl.PullRequests;
using Assembly.Infrastructure.Chat.SourceControl;
using Assembly.Infrastructure.Git;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class GitCheckoutScopeTests
{
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed class StubProjects(Dictionary<string, (string Machine, string Root)> projects) : IProjectRoots
    {
        public ProjectRoot? Find(string id) => projects.TryGetValue(id, out var p) ? new(id, p.Machine, p.Root, false) : null;
        public ProjectRoot? Find(IReadScope scope, string id) => Find(id);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Dictionary<string, (string Machine, string Root)> Projects = [];
        internal readonly string Home = Directory.CreateTempSubdirectory("git-inspection-").FullName;
        internal string Root => Path.Combine(Home, "own");
        internal string Linked => Path.Combine(Home, "linked");
        internal readonly TwoHosts Hosts = new(forwardCommands: true);
        internal TwoHosts.Host Owner => Remote ? Hosts.Remote : Hosts.Mac;
        internal readonly CountingGit Git = new();
        internal readonly CheckoutWrites LocalWrites = new();
        private readonly CountingGit _desktopGit = new();
        private readonly BackgroundWork _localWork = new(TimeProvider.System, TextWriter.Null), _remoteWork = new(TimeProvider.System, TextWriter.Null);
        private GitOperations _localOperations = null!, _remoteOperations = null!;
        internal SocketDispatcher Dispatcher = null!;
        internal string Session = null!, Sha = null!;
        internal ExpectedSource Source = null!;
        internal bool Remote;
        internal GuardLease? Captured;
        private long _request;

        internal static async Task<Fixture> Start(bool remote = false, bool oldOwner = false)
        {
            var f = new Fixture { Remote = remote };
            try
            {
                await Repository(f.Root);
                await Run(f.Root, "worktree", "add", "-b", "linked", f.Linked);
                await File.WriteAllTextAsync(Path.Combine(f.Linked, "tracked.txt"), "linked commit\n");
                await Run(f.Linked, "add", "tracked.txt");
                await Run(f.Linked, "-c", "user.name=Git Test", "-c", "user.email=git@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", "linked commit");
                f.Sha = (await Run(f.Linked, "rev-parse", "HEAD")).Trim();
                await File.WriteAllTextAsync(Path.Combine(f.Linked, "tracked.txt"), "working changes\n");
                f._localOperations = new(f._localWork, f.Hosts.Mac.Topics, TimeProvider.System, f.Hosts.Mac.Fixture.Machine);
                f._remoteOperations = new(f._remoteWork, f.Hosts.Remote.Topics, TimeProvider.System, f.Hosts.Remote.Fixture.Machine);
                ICallRegistry local = null!;
                f.Hosts.Mac.AdditionalFeatures = calls => { local = calls; f.Dispatcher = (SocketDispatcher)calls; };
                f.Hosts.Remote.AdditionalFeatures = calls =>
                {
                    var routes = new SourceControlFeature(f.Hosts.Remote.Sessions, f.Hosts.Remote.Fixture.Db, f._remoteOperations, null, null, f.Git, new StubProjects([]));
                    routes.Register(new ReadRegistry(calls, f, oldOwner));
                    new SourceControlWrites(f.Hosts.Remote.Sessions, f.Git, new CheckoutWrites(), TimeProvider.System, routes).Register(calls);
                };
                await f.Hosts.Start(); await f.Hosts.StartMirror();
                var localGit = remote ? f._desktopGit : f.Git;
                var localRoutes = new SourceControlFeature(f.Hosts.Mac.Sessions, f.Hosts.Mac.Fixture.Db, f._localOperations, f.Hosts.Mac.Forwarder,
                    f.Hosts.Connections, localGit, new StubProjects(f.Projects));
                localRoutes.Register(new ReadRegistry(local, f));
                new SourceControlWrites(f.Hosts.Mac.Sessions, localGit, f.LocalWrites, TimeProvider.System, localRoutes).Register(local);
                f.Session = f.Owner.Sessions.Create("fixture", "", workingFolder: f.Root).SessionId;
                if (remote) f.Hosts.Mac.Sessions.Add(f.Session, "fixture", "", "workbox-test", workingFolder: "/untrusted-desktop-copy");
                f.Source = remote ? f.Hosts.Mac.Fixture.Db.QueryFirst(
                    "SELECT machine,generation,name FROM engine_mirror WHERE state='live' AND name='workbox-test'",
                    r => new ExpectedSource(r.Text(0), r.Long(1), r.Text(2)), default)! :
                    new(f.Hosts.Mac.Fixture.Machine, 0, null);
                f.Git.Reset(); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal void AddEngineSession(string folder, string title, string state, string activity) =>
            Hosts.Mac.Fixture.Db.Writer.Write("fixture.session", scope => scope.Execute("""
                INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,folder,title,state,activity_state,holder,epoch,cancel_requested)
                SELECT $id,0,machine_id,0,0,'claude',$folder,$title,$state,$activity,'local',0,0 FROM engine_meta WHERE singleton=1
                """, new SqlArg("$id", Guid.NewGuid().ToString()), new SqlArg("$folder", folder), new SqlArg("$title", title),
                new SqlArg("$state", state), new SqlArg("$activity", activity)));
        /// <summary>A prepared fetch, waiting for its turn on the folder's lease.</summary>
        internal Func<CancellationToken, Task<JsonElement>> Operation(string session, string root)
        {
            var gh = new GhCli();
            var reader = new PullRequestReader(Git, gh, Home, new GitTextPatch(Git));
            var prepared = new GitSessionOperations(Hosts.Mac.Sessions, Git, new PullRequestWriter(Git, gh, reader), LocalWrites, _localOperations,
                new(Git, new UnavailableUtility()), reader, Hosts.Mac.Fixture.Db);
            return prepared.Prepare(new GitOperationStart("instance", Guid.NewGuid(), session, "fetch", default, ExpectedCheckoutRoot: root));
        }
        internal object Args(bool selected = false, string? checkout = null) => new
        {
            session = Session,
            expectedSource = Source,
            checkoutPath = checkout ?? (selected ? Linked : null),
            expectedCheckoutRoot = selected || checkout is not null ? Root : null,
            path = "tracked.txt",
            sha = Sha
        };
        internal void SetRoot(string? root) => Owner.Fixture.Db.Writer.Write("fixture.recorded_root", scope =>
            scope.Execute("UPDATE session_info SET working_folder=$root WHERE session_id=$id", new SqlArg("$root", root), new SqlArg("$id", Session)));
        internal async Task<ClientWebSocket> Open()
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new UriBuilder(Hosts.Mac.Address) { Scheme = "ws", Path = "/socket" }.Uri, default);
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"t\":\"hello\",\"token\":\"isolated-token\",\"protocol\":1}"), WebSocketMessageType.Text, true, default);
            Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString()); return socket;
        }
        internal async Task<JsonElement> Call(WebSocket socket, string method, object args)
        {
            var frame = JsonSerializer.SerializeToUtf8Bytes(new { t = "call", id = Interlocked.Increment(ref _request), feature = "git", method, args }, Wire);
            await socket.SendAsync(frame, WebSocketMessageType.Text, true, default); return await Read(socket);
        }
        internal static async Task<JsonElement> Read(WebSocket socket)
        {
            using var bytes = new MemoryStream(); var buffer = new byte[65536]; WebSocketReceiveResult read;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            do { read = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token); bytes.Write(buffer, 0, read.Count); } while (!read.EndOfMessage);
            using var json = JsonDocument.Parse(bytes.ToArray()); return json.RootElement.Clone();
        }
        internal static async Task Repository(string root)
        {
            Directory.CreateDirectory(root); await Run(root, "init", "-b", "main");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "original\n"); await Run(root, "add", "tracked.txt");
            await Run(root, "-c", "user.name=Git Test", "-c", "user.email=git@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", "initial");
            await Run(root, "update-ref", "refs/remotes/origin/main", "HEAD");
            await Run(root, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        }
        internal static async Task<string> Run(string root, params string[] args)
        {
            var result = await new GitCli().RunAsync(root, args, null, null, default); Assert.Equal(0, result.ExitCode); return result.Output;
        }
        public async ValueTask DisposeAsync()
        {
            await Hosts.DisposeAsync();
            if (_localOperations is not null) await _localOperations.DisposeAsync();
            if (_remoteOperations is not null) await _remoteOperations.DisposeAsync();
            await _localWork.DisposeAsync(); await _remoteWork.DisposeAsync(); Directory.Delete(Home, true);
        }
    }

    private sealed class CountingGit : IGit
    {
        internal ConcurrentQueue<string[]> Commands { get; } = new();
        internal int Count => Commands.Count;
        internal Func<string, IReadOnlyList<string>, Task>? BeforeRead;
        internal void Reset() => Commands.Clear();
        public Task<GitRun> RunAsync(string folder, IReadOnlyList<string> args, string? input, TimeSpan? limit, CancellationToken ct) =>
            new GitCli().RunAsync(folder, args, input, limit, ct);
        public async IAsyncEnumerable<string> ReadAsync(string folder, IReadOnlyList<string> args, IReadOnlyList<string>? input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Commands.Enqueue(args.ToArray());
            if (BeforeRead is { } before) await before(folder, args).WaitAsync(ct);
            await foreach (var record in new GitCli().ReadAsync(folder, args, input, ct)) yield return record;
        }
    }
    private sealed class GuardLease(IExpectedSourceGuard inner) : IExpectedSourceGuard
    {
        internal bool Disposed;
        public CancellationToken Changed => inner.Changed;
        public void EnsureCurrent() => inner.EnsureCurrent();
        public void Dispose() { if (Disposed) return; Disposed = true; inner.Dispose(); }
    }
}
