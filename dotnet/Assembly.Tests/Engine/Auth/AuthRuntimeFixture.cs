using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Hosting;
using Assembly.Engine.Providers;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Engine;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Auth;

internal sealed class AuthRuntimeFixture : IAsyncDisposable
{
    internal Topics Topics { get; } = new();
    internal EngineFixture Core { get; }
    internal WriterGate Database { get; }
    internal EngineRuntime Runtime { get; }
    internal EngineAuthSink Sink { get; }
    internal AuthRuntimeFixture()
    {
        Core = new(topics: Topics); Database = new(Core.Db);
        Runtime = EngineRuntime.Start(Database, Topics, Core.Time, Core.Limits,
            effectsReady: new TaskCompletionSource().Task, isConfiguredProvider: (p, a) => p == "codex" && a == "");
        Sink = new(Runtime, Core.Time);
    }
    internal CommandResult Apply(EngineCommand command, string id, long version = 0, EngineActor? actor = null) =>
        ((IEngineRuntime)Runtime).Write(s => Runtime.Commands.Apply(s,
            new(Guid.NewGuid(), Runtime.Machine, actor ?? new(ActorKind.Window, "owner"), id, version, 0, Core.Time.GetUtcNow().ToUnixTimeMilliseconds()), command));
    internal ProviderProbeBinding Probe()
    {
        Assert.True(Apply(new CheckProvider(), "codex/").Applied);
        Assert.True(Apply(new ClaimEffect(), "probe_provider/codex//1", actor: new(ActorKind.System, "engine.worker")).Applied);
        return new("codex/", "probe_provider/codex//1", 1, 1);
    }
    internal SignInAttemptBinding Begin()
    {
        Assert.True(Apply(new StartSignIn("codex", "", null, null), "operation").Applied);
        Assert.True(Apply(new ClaimEffect(), "begin_sign_in/operation", actor: new(ActorKind.System, "engine.worker")).Applied);
        Assert.True(Apply(new CompleteEffect(new(EffectDisposition.Confirmed,
            JsonSerializer.SerializeToElement(new SignInEffectData("operation", "started"), EngineJson.Default.SignInEffectData), null)),
            "begin_sign_in/operation", 1, new(ActorKind.System, "engine.worker")).Applied);
        return new("operation", "codex/", "begin_sign_in/operation", 1);
    }
    internal static string Facts(string name = "Model") => JsonSerializer.Serialize(new
    {
        provider = "codex",
        name = "Codex",
        adapter = "Adapter",
        version = (string?)null,
        installation = "installed",
        auth = "unverified",
        problem = (string?)null,
        models = new[] { new { id = "model", name } },
        methods = new[] { new { methodId = "login", name = "Login", kind = "agent" } },
        checkedMs = 123
    });
    public async ValueTask DisposeAsync() { Database.Release(); try { await Runtime.DisposeAsync(); } finally { Database.Dispose(); Core.Dispose(); } }

    internal sealed class WriterGate(IAppDatabase inner) : IAppDatabase, IAtomicWriter, IDisposable
    {
        private int _armed;
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm() => Interlocked.Exchange(ref _armed, 1);
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
        public IAtomicWriter Writer => this;
        public T Write<T>(string what, Func<IWriteScope, T> body)
        {
            if (what == "engine.command" && Interlocked.Exchange(ref _armed, 0) != 0)
            { Entered.TrySetResult(); if (!_release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Writer gate was not released."); }
            return inner.Writer.Write(what, body);
        }
        public IReadSnapshot OpenSnapshot(CancellationToken cancellationToken) => inner.OpenSnapshot(cancellationToken);
        public List<T> Query<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args) => inner.Query(sql, map, cancel, args);
        public T? QueryFirst<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args) => inner.QueryFirst(sql, map, cancel, args);
    }
}
