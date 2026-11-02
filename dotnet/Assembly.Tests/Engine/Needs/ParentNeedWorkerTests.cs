using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Engine.Needs;

public sealed class ParentNeedWorkerTests
{
    [Theory]
    [InlineData("pending")]
    [InlineData("uncertain")]
    [InlineData("inflight")]
    [InlineData("unavailable")]
    public async Task More_than_one_worker_page_of_coordinator_intents_does_not_block_or_enter_provider_execution(string state)
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var f = new EngineFixture(topics: topics); f.SeedSession();
        f.Db.Writer.Write("test.intent-census", s =>
        {
            s.Execute("""
                WITH RECURSIVE ids(n) AS (VALUES(0) UNION ALL SELECT n+1 FROM ids WHERE n<128)
                INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,kind,payload_json,state,attempt,next_ms)
                SELECT printf('intent-%03d',n),0,$machine,1000,1000,0,'s','parent_need','{}',$state,0,1000 FROM ids
                """, new Assembly.Application.Common.Database.SqlArg("$machine", f.Machine), new("$state", state));
            return s.Execute("""
                INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,kind,payload_json,state,attempt)
                VALUES('ordinary',0,$machine,1000,1000,1,'s','stop','{}','pending',0)
                """, new Assembly.Application.Common.Database.SqlArg("$machine", f.Machine));
        });
        var effects = new ObservingEffects();
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits, effects);
        var events = Channel.CreateUnbounded<EngineEvent>();
        using var subscription = runtime.Publisher.Register(e => events.Writer.TryWrite(e), out _);
        effects.Release.TrySetResult();
        runtime.EffectsAvailable();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var e = await events.Reader.ReadAsync(deadline.Token);
            if (e.EntityId == "ordinary" && e.Data.State == "done") break;
        }
        Assert.Equal(["ordinary"], effects.Executed.ToArray()); Assert.Empty(effects.Reconciled);
        Assert.Equal(129, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='parent_need' AND version=0 AND attempt=0 AND next_ms=1000 AND state=$state", new Assembly.Application.Common.Database.SqlArg("$state", state)));
        Assert.Equal("done", f.State("engine_outbox", "ordinary"));
    }

    private sealed class ObservingEffects : IEngineEffects
    {
        internal ConcurrentQueue<string> Executed { get; } = new();
        internal ConcurrentQueue<string> Reconciled { get; } = new();
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<EffectReceipt> ExecuteAsync(Effect effect, CancellationToken ct)
        {
            Executed.Enqueue(effect.Id);
            await Release.Task.WaitAsync(ct);
            return new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new SessionEffectData("idle", null, null), Assembly.Engine.EngineJson.Default.SessionEffectData), null);
        }
        public Task<EffectReceipt> ReconcileAsync(Effect effect, CancellationToken ct)
        {
            Reconciled.Enqueue(effect.Id);
            return Task.FromResult(new EffectReceipt(EffectDisposition.Unknown, JsonSerializer.SerializeToElement((string?)null), null));
        }
    }
}
