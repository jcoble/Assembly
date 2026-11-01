using System.Net;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Remote;

// Both roles own a real isolated database, EngineRuntime, writer and authenticated backend socket.
internal sealed class TwoHosts : IAsyncDisposable
{
    internal sealed class Host : IAsyncDisposable
    {
        public TopicSet Topics { get; } = new();
        public EngineFixture Fixture { get; }
        public EngineRuntime Runtime { get; private set; }
        public WebApplication App { get; private set; } = null!;
        public Uri Address { get; private set; } = null!;
        public SocketEndpoint Endpoint { get; private set; } = null!;
        private bool _running;
        public int MachineCalls;
        public event Action? RetentionCommitted;
        private EngineRuntime StartRuntime() => EngineRuntime.Start(new ObservedDatabase(Fixture.Db, () => RetentionCommitted?.Invoke()), Topics, Fixture.Time, new(() => 1, () => 5));
        private readonly bool _windowCalls;
        public Host(bool windowCalls = false) { _windowCalls = windowCalls; Fixture = new(topics: Topics); Runtime = StartRuntime(); }
        public async Task Start(int port = 0)
        {
            var calls = new SocketDispatcher(Fixture.Db.Writer, Topics, Fixture.Time, NullLogger.Instance, new EngineSocketStream(Runtime));
            if (_windowCalls) new EngineCalls(Runtime, Fixture.Time).Register(calls);
            else calls.Read("engine", "machine", EngineSocketJson.Default.JsonElement, EngineSocketJson.Default.MachineInfo, (_, c) => { Interlocked.Increment(ref MachineCalls); return Task.FromResult(Runtime.Reader.Machine(c.Cancellation)); });
            calls.Topic("sessions", SessionsJson.Default.SessionItem, s => s.SessionId, (_, _) => []);
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
            App = builder.Build(); App.UseWebSockets();
            Endpoint = new SocketEndpoint("isolated-token", calls, Fixture.Machine, Fixture.Time);
            var bulk = new SocketEndpoint("isolated-token", calls, Fixture.Machine, Fixture.Time, bulk: true);
            App.Map("/socket", Endpoint.HandleAsync); App.Map("/socket/bulk", bulk.HandleAsync);
            App.Lifetime.ApplicationStopping.Register(Endpoint.CloseAll); App.Lifetime.ApplicationStopping.Register(bulk.CloseAll);
            await App.StartAsync();
            _running = true;
            Address = new(App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        }
        public long Through => Runtime.Reader.HighWatermark(default);
        public void Commit(int count, string title = "current", string eventKind = "session.changed") => Fixture.Db.Writer.Write("test.remote", s =>
        {
            for (var i = 0; i < count; i++)
            {
                var version = s.QueryFirst("SELECT version FROM engine_session WHERE id='s'", r => r.Long(0)) + 1;
                s.Execute("UPDATE engine_session SET version=$v,title=$t WHERE id='s'", new("$v", version), new("$t", title));
                EngineJournal.Commit(s, new(Guid.NewGuid(), Fixture.Machine, new(ActorKind.System, "test"), "s", version - 1, 0, 1000), version, 0,
                    [new(eventKind, "session", "s", version, false, new("s", null, null, null, "idle", null))], []);
            }
            return 0;
        });
        public async Task Restore(string path)
        {
            var port = Address.Port;
            await Stop();
            Fixture.RestoreFrom(path);
            Runtime = StartRuntime();
            await Start(port);
        }
        public async Task Stop()
        {
            if (!_running) return;
            await App.DisposeAsync(); await Runtime.DisposeAsync(); _running = false;
        }
        public async Task Restart()
        {
            var port = Address.Port;
            Fixture.Reopen();
            Runtime = StartRuntime();
            await Start(port);
        }
        private sealed class ObservedDatabase(IAppDatabase db, Action retained) : IAppDatabase
        {
            public IAtomicWriter Writer { get; } = new ObservedWriter(db.Writer, retained);
            public IReadSnapshot OpenSnapshot(CancellationToken ct) => db.OpenSnapshot(ct);
            public List<T> Query<T>(string sql, Func<IRow, T> map, CancellationToken ct, params SqlArg[] args) => db.Query(sql, map, ct, args);
            public T? QueryFirst<T>(string sql, Func<IRow, T> map, CancellationToken ct, params SqlArg[] args) => db.QueryFirst(sql, map, ct, args);
            private sealed class ObservedWriter(IAtomicWriter writer, Action retained) : IAtomicWriter
            {
                public T Write<T>(string what, Func<IWriteScope, T> write)
                {
                    var result = writer.Write(what, write);
                    if (what == "engine.mirror.retention") retained();
                    return result;
                }
            }
        }
        public async ValueTask DisposeAsync() { await Stop(); Fixture.Dispose(); }
    }

    public Host Remote { get; private set; } = new();
    public Host Mac { get; }
    public TwoHosts(bool windowCalls = false) { Mac = new(windowCalls); }
    public EngineMirror Mirror { get; private set; } = null!;
    public StringWriter MirrorLog { get; } = new();
    private RemoteMachine? _machine;
    private LiveHub? _hub;
    public async Task Start() { await Remote.Start(); await Mac.Start(); Remote.Fixture.SeedSession(); }
    public async Task StartMirror(TimeProvider? mirrorTime = null)
    {
        var store = RowStore.Open(Mac.Fixture.Db);
        _hub = new LiveHub(store, topics: Mac.Topics);
        var sessions = SessionStore.Open(Mac.Fixture.Db, Mac.Fixture.Time, "/isolated");
        _machine = new("workbox-test", Remote.Address, "isolated-token", _hub, sessions, TextWriter.Null, new MachineStatus(Mac.Topics, []), topics: Mac.Topics);
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = Mac.Topics.Subscribe("__engine_mirrored", Remote.Fixture.Machine, _ => { }, (_, _) =>
        {
            if (Mac.Fixture.Number("SELECT COALESCE(MAX(through),-1) FROM engine_mirror WHERE state='live' AND machine=$m",
                new SqlArg("$m", Remote.Fixture.Machine)) >= Remote.Through) applied.TrySetResult();
        });
        try
        {
            Mirror = new(_machine, Mac.Fixture.Db.Writer, mirrorTime ?? TimeProvider.System, MirrorLog);
            await _machine.ProbeAsync(default);
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { Mac.Topics.Unsubscribe(sub); }
    }
    public async Task Reached(long through)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe() { if (Mac.Fixture.Number("SELECT COALESCE(MAX(through),-1) FROM engine_mirror WHERE state='live' AND machine=$m", new SqlArg("$m", Remote.Fixture.Machine)) >= through) done.TrySetResult(); }
        Mirror.CaughtUp += Observe;
        var sub = Mac.Topics.Subscribe("__engine_mirrored", null, _ => { }, (_, _) => Observe());
        try { Observe(); await done.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { Mac.Topics.Unsubscribe(sub); Mirror.CaughtUp -= Observe; }
    }
    public async Task StopMirror() { if (_machine is null) return; await Mirror.DisposeAsync(); await _machine.DisposeAsync(); _machine = null; _hub!.Dispose(); }
    public async Task StopMac() { await StopMirror(); await Mac.Stop(); }
    public async Task StartMac() { await Mac.Restart(); await StartMirror(); }
    public async Task ReplaceRemote() { await Remote.DisposeAsync(); Remote = new(); await Remote.Start(); Remote.Fixture.SeedSession(); }
    public string? RowTitle => Mac.Fixture.Text("SELECT json_extract(r.row_json,'$.title') FROM engine_mirror_row r JOIN engine_mirror m ON m.machine=r.machine AND m.generation=r.generation WHERE m.state='live' AND r.entity_id='s'");
    public async ValueTask DisposeAsync() { await StopMirror(); await Mac.DisposeAsync(); await Remote.DisposeAsync(); MirrorLog.Dispose(); }
}
