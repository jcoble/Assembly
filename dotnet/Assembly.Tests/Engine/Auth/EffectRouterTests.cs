using System.Text.Json;
using Assembly.Engine.Effects;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Auth;
using Assembly.Tests.Engine.Delivery;

namespace Assembly.Tests.Engine.Auth;

public sealed class EffectRouterTests
{
    [Fact]
    public async Task Real_worker_wrapped_adapter_commits_positive_ack_with_message_and_effect()
    {
        await using var f = new AdapterDeliveryFixture(worker: true, router: true);
        var message = f.Queue("exact\0body", "client"); var agent = await f.Launcher.NextAsync(); await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt"); await agent.InsertAsync(prompt, "actual-provider");
        try
        {
            await f.StateAsync("engine_message", message, "next_turn");
            using (var snapshot = f.Db.OpenSnapshot(default))
            {
                var row = f.Rows.UserDeliveryOfClient(snapshot, f.Session.SessionId, "client")!;
                Assert.Equal("prompt_inserted", row.Acknowledgment?.Kind); Assert.Equal("actual-provider", row.Acknowledgment?.ProviderMessageId);
                Assert.Equal("exact\0body", row.Text);
            }
            Assert.Equal("done", f.Db.QueryFirst("SELECT state FROM engine_outbox WHERE kind='send'", r => r.Text(0), default));
        }
        finally
        {
            await agent.UpdateAsync("{\"sessionUpdate\":\"state_update\",\"state\":\"idle\",\"stopReason\":\"end_turn\"}"); await agent.CloseAsync();
        }
    }

    [Theory]
    [InlineData("ack")]
    [InlineData("message")]
    [InlineData("receipt")]
    public async Task Wrapped_adapter_completion_fault_preserves_unknown_reservation_without_positive_ack(string fault)
    {
        await using var f = new AdapterDeliveryFixture(worker: true, router: true);
        f.Db.Writer.Write("test.router.fault", s => s.Execute(fault switch
        {
            "ack" => "CREATE TRIGGER router_fault BEFORE UPDATE OF payload ON rows BEGIN SELECT RAISE(ABORT,'ack fault'); END",
            "message" => "CREATE TRIGGER router_fault BEFORE UPDATE OF state ON engine_message WHEN NEW.state='next_turn' BEGIN SELECT RAISE(ABORT,'message fault'); END",
            _ => "CREATE TRIGGER router_fault BEFORE UPDATE OF receipt_json ON engine_outbox WHEN json_extract(NEW.receipt_json,'$.disposition')=0 BEGIN SELECT RAISE(ABORT,'receipt fault'); END"
        }));
        var message = f.Queue(); var agent = await f.Launcher.NextAsync(); await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt"); await agent.InsertAsync(prompt, "accepted-once");
        var effect = f.Db.QueryFirst("SELECT id FROM engine_outbox WHERE kind='send'", r => r.Text(0), default)!;
        try
        {
            await f.StateAsync("engine_outbox", effect, "uncertain");
            Assert.Equal("queued", f.State("engine_message", message)); Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message WHERE id=$id", message));
            using var snapshot = f.Db.OpenSnapshot(default); Assert.Null(f.Rows.UserDeliveryOfClient(snapshot, f.Session.SessionId, "C")!.Acknowledgment);
            Assert.Equal(1, f.Launcher.Launches);
        }
        finally
        {
            f.Db.Writer.Write("test.router.unblock", s => s.Execute("DROP TRIGGER router_fault"));
            await agent.UpdateAsync("{\"sessionUpdate\":\"state_update\",\"state\":\"idle\",\"stopReason\":\"end_turn\"}"); await agent.CloseAsync();
        }
    }

    [Fact]
    public async Task Runtime_seals_router_before_returning_started_owner()
    {
        await using var f = new AdapterDeliveryFixture(worker: true, router: true);
        Assert.Throws<InvalidOperationException>(() => f.Router!.Add("probe_provider", new Handler()));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("unrelated")]
    public async Task Registration_refuses_existing_adapter_kinds_and_arbitrary_new_kinds(string kind)
    {
        await using var f = new AdapterDeliveryFixture(); await using var router = new EffectRouter(f.Effects);
        Assert.Throws<ArgumentException>(() => router.Add(kind, new Handler()));
    }

    [Fact]
    public async Task Duplicate_registration_is_refused_without_replacing_original_owner()
    {
        await using var f = new AdapterDeliveryFixture(); await using var router = new EffectRouter(f.Effects);
        router.Add("probe_provider", new Handler()); Assert.Throws<InvalidOperationException>(() => router.Add("probe_provider", new Handler()));
    }

    [Fact]
    public async Task Both_paths_dispatch_same_registered_owner_and_dispose_each_distinct_owner_once()
    {
        await using var f = new AdapterDeliveryFixture(); var owner = new Handler(); await using var router = new EffectRouter(f.Effects);
        router.Add("probe_provider", owner).Add("begin_sign_in", owner)
            .Add("workflow_setup", owner).Add("workflow_workspace", owner); router.Seal();
        foreach (var kind in new[] { "probe_provider", "workflow_setup", "workflow_workspace" })
        {
            var effect = Effect(kind);
            Assert.Equal(EffectDisposition.Confirmed, (await router.ExecuteAsync(effect, default)).Disposition);
            Assert.Equal(EffectDisposition.Confirmed, (await router.ReconcileAsync(effect, default)).Disposition);
            Assert.Same(effect, owner.Executed); Assert.Same(effect, owner.Reconciled);
        }
        await router.DisposeAsync(); await router.DisposeAsync(); Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task Unknown_kind_is_honestly_unavailable_on_execute_and_reconcile()
    {
        await using var f = new AdapterDeliveryFixture(); await using var router = new EffectRouter(f.Effects); router.Seal();
        foreach (var receipt in new[] { await router.ExecuteAsync(Effect("unknown"), default), await router.ReconcileAsync(Effect("unknown"), default) })
        { Assert.Equal(EffectDisposition.Unavailable, receipt.Disposition); Assert.Equal("no_handler", receipt.Error); }
    }

    [Fact]
    public async Task Runtime_shutdown_disposes_registered_router_owner_once_after_worker_join()
    {
        await using var f = new AdapterDeliveryFixture(); await f.Runtime.DisposeAsync(); var owner = new Handler();
        var runtime = EngineRuntime.Start(f.Db, f.Topics, f.Time, new(() => 5, () => 10),
            effectsFactory: _ => new EffectRouter(f.Effects).Add("probe_provider", owner));
        await runtime.DisposeAsync(); await runtime.DisposeAsync(); Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task Handler_dispose_fault_still_disposes_other_owners_and_runtime_cancellation_owner()
    {
        await using var f = new AdapterDeliveryFixture(); await f.Runtime.DisposeAsync();
        var first = new Handler { ThrowOnDispose = true }; var second = new Handler();
        var runtime = EngineRuntime.Start(f.Db, f.Topics, f.Time, new(() => 5, () => 10),
            effectsFactory: _ => new EffectRouter(f.Effects).Add("probe_provider", first).Add("begin_sign_in", second));
        await Assert.ThrowsAnyAsync<Exception>(async () => await runtime.DisposeAsync());
        Assert.Equal(1, first.Disposals); Assert.Equal(1, second.Disposals);
        Assert.Throws<ObjectDisposedException>(() => runtime.Stopping);
    }

    private static Effect Effect(string kind) => new("effect", kind, null, null, JsonSerializer.SerializeToElement((string?)null), 1, "provider/codex/");
    private sealed class Handler : IEngineEffects, IAsyncDisposable
    {
        public Effect? Executed { get; private set; }
        public Effect? Reconciled { get; private set; }
        public int Disposals { get; private set; }
        public bool ThrowOnDispose { get; init; }
        public Task<EffectReceipt> ExecuteAsync(Effect effect, CancellationToken cancellationToken) { Executed = effect; return Receipt(); }
        public Task<EffectReceipt> ReconcileAsync(Effect effect, CancellationToken cancellationToken) { Reconciled = effect; return Receipt(); }
        private static Task<EffectReceipt> Receipt() => Task.FromResult(new EffectReceipt(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement((string?)null), null));
        public ValueTask DisposeAsync() { Disposals++; if (ThrowOnDispose) throw new InvalidOperationException("owned dispose fault"); return ValueTask.CompletedTask; }
    }
}
