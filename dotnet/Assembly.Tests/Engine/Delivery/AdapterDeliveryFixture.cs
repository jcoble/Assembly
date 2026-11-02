using Assembly.Tests.Engine;
using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Assembly.Engine.Persistence;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Persistence;
using Assembly.Tests.Features.Chat.Sessions;
using Microsoft.Extensions.Time.Testing;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Delivery;

internal sealed class AdapterDeliveryFixture : IAsyncDisposable
{
    private readonly TempDatabase _file = new();
    private readonly bool _worker;
    private readonly bool _legacy;
    private IDisposable _subscription = null!;
    public Topics Topics { get; private set; } = null!;
    public AppDatabase Db { get; private set; } = null!;
    public RowStore Rows { get; private set; } = null!;
    public LiveHub Hub { get; private set; } = null!;
    public SessionStore Sessions { get; private set; } = null!;
    public SessionInfo Session { get; private set; } = null!;
    public FakeLauncher Launcher { get; } = new();
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.FromUnixTimeMilliseconds(1000));
    public BackgroundWork Work { get; private set; } = null!;
    public SessionRegistry Hosts { get; private set; } = null!;
    public EngineRuntime Runtime { get; private set; } = null!;
    public AdapterEffects Effects { get; private set; } = null!;
    public EngineCommands Commands { get; private set; } = null!;
    public Channel<EngineEvent> Events { get; } = Channel.CreateUnbounded<EngineEvent>();
    public AdapterDeliveryFixture(bool worker = false, bool legacy = false) { _worker = worker; _legacy = legacy; Open(); }
    private void Open()
    {
        var topics = Topics = new Topics(); Db = AppDatabase.Open(_file.Path, Time, topics);
        Rows = RowStore.Open(Db);
        if (_legacy && Session is null) Db.Writer.Write("test.frozen-legacy", s => s.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Engine", "Fixtures", "old-v2.sql"))));
        Hub = new LiveHub(Rows); Sessions = SessionStore.Open(Db, Time, "checkouts");
        Session = Session is null ? Sessions.Create("claude", "https://github.com/o/r") : Sessions.Get(Session.SessionId)!;
        Work = new BackgroundWork(Time, TextWriter.Null);
        var cutover = new SessionCutover(Db, Sessions, Launcher, "checkouts", Time);
        AdapterEffects Create(EngineRuntime runtime)
        {
            Assert.NotNull(Sessions.Get(Session.SessionId));
            Assert.Equal(5, Db.QueryFirst("SELECT schema_version FROM engine_meta", r => r.Long(0), default));
            Hosts = new SessionRegistry(Hub, Sessions, Launcher, Time, TextWriter.Null, Work, feed: new EngineFeed(runtime, TextWriter.Null));
            return Effects = new AdapterEffects(Db, Hosts, Sessions, Rows);
        }
        using var ready = new ManualResetEventSlim();
        using var readiness = topics.Subscribe("test.worker.ready", null, _ => { }, (_, _) => ready.Set());
        Runtime = EngineRuntime.Start(Db, topics, Time, new(() => 5, () => 10), cutover: cutover,
            effectsFactory: _worker ? Create : null, retention: new ReadyRetention());
        Assert.True(ready.Wait(TimeSpan.FromSeconds(8)));
        if (!_worker) Create(Runtime);
        Commands = _worker ? Runtime.Commands : new EngineCommands(Runtime.Machine, new(() => 5, () => 10), projectCompletion: Effects.ProjectCompletion);
        _subscription = Runtime.Publisher.Register(e => Events.Writer.TryWrite(e), out _);
    }
    public string Queue(string text = "exact body", string client = "C", bool raw = true, IReadOnlyList<string>? refs = null, string? sessionId = null)
    {
        sessionId ??= Session.SessionId;
        var id = raw ? OwnerPromptIdentity.MessageId(sessionId, client) : "message-" + client;
        if (raw)
        {
            var request = new SendOwnerPrompt(sessionId, text, client, refs ?? [], null);
            var prepared = OwnerPromptFingerprint.Prepare(Runtime.Machine, request, default);
            var context = Context(id, 0) with { Actor = new(ActorKind.Window, "owner"), CommandId = OwnerPromptIdentity.CommandId(Runtime.Machine, sessionId, client), Epoch = 0 };
            var result = Db.Writer.Write("test.owner", s => Commands.ApplyOwnerPrompt(s, context, prepared)); Assert.True(result.Applied, result.Error);
        }
        else
        {
            var result = Apply(new SendMessage(null, sessionId, text, "prompt"), id, 0, new(ActorKind.Window, "owner")); Assert.True(result.Applied, result.Error);
        }
        return id;
    }
    public Effect Claim(string message)
    {
        var id = Db.QueryFirst("SELECT id FROM engine_outbox WHERE kind='send' AND json_extract(payload_json,'$.messageId')=$m ORDER BY event_seq DESC,rowid DESC LIMIT 1", r => r.Text(0), default, new SqlArg("$m", message))!;
        return ClaimEffect(id);
    }
    public Effect ClaimEffect(string id)
    {
        var result = Apply(new ClaimEffect(), id, Number("SELECT version FROM engine_outbox WHERE id=$id", id)); Assert.True(result.Applied, result.Error);
        return EffectTransitions.Read(Db, id, default)!;
    }
    public CommandContext Context(string id, long version) => new(Guid.NewGuid(), Runtime.Machine, new(ActorKind.System, "test"), id, version,
        Number("SELECT epoch FROM engine_session WHERE id=$id", Session.SessionId), Time.GetUtcNow().ToUnixTimeMilliseconds());
    public CommandResult Apply(EngineCommand command, string id, long version, EngineActor? actor = null) =>
        Db.Writer.Write("test.command", s => Commands.Apply(s, actor is null ? Context(id, version) : Context(id, version) with { Actor = actor }, command));
    public CommandResult Complete(Effect effect, EffectReceipt receipt) => Apply(new CompleteEffect(receipt), effect.Id, Number("SELECT version FROM engine_outbox WHERE id=$id", effect.Id));
    public long Number(string sql, string id) => Db.QueryFirst(sql, r => r.Long(0), default, new SqlArg("$id", id));
    public string? State(string table, string id) => Db.QueryFirst($"SELECT state FROM {table} WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", id));
    public async Task StateAsync(string table, string id, string state)
    {
        if (State(table, id) == state) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true) { var e = await Events.Reader.ReadAsync(timeout.Token); if (e.EntityId == id && e.Data.State == state) return; }
    }
    public async Task RestartAsync()
    {
        await CloseAsync(); Open();
    }
    private async Task CloseAsync()
    {
        await Hosts.DisposeAsync(); await Runtime.DisposeAsync(); _subscription.Dispose(); await Work.DisposeAsync(); Hub.Dispose(); Db.Dispose();
    }
    private sealed class ReadyRetention : IEngineRetention
    {
        private bool _ready;
        public long? NextDeadlineMs(IReadScope scope, long now) => null;
        public void Apply(IWriteScope scope, long now)
        {
            if (_ready) return;
            scope.Publish("test.worker.ready", null, now, global::Assembly.Engine.EngineJson.Default.Int64);
            _ready = true;
        }
    }
    public async ValueTask DisposeAsync() { await CloseAsync(); _file.Dispose(); }
}
