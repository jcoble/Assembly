using Assembly.Tests.Engine;
using Assembly.Application.Common.Engine;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Engine.Effects;
using Assembly.Engine.Handoffs;

namespace Assembly.Tests.Engine.Delivery;

public sealed partial class AdapterEffectsTests
{
    [Fact]
    public async Task Cancellation_after_prompt_submission_never_authorizes_a_second_provider_send()
    {
        await using var f = new AdapterDeliveryFixture(); var effect = f.Claim(f.Queue());
        using var canceled = new CancellationTokenSource();
        var sending = f.Effects.ExecuteAsync(effect, canceled.Token);
        var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await agent.InsertAsync(prompt, "accepted-after-cancellation");
        await f.StateAsync("engine_session", f.Session.SessionId, "running");
        Assert.Equal(EffectDisposition.Unknown, (await f.Effects.ReconcileAsync(effect, default)).Disposition);
        Assert.Equal(EffectDisposition.Unknown, (await f.Effects.ExecuteAsync(effect, default)).Disposition);
        Assert.Single(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.Kind == "user");
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
        await f.RestartAsync(); Assert.Equal(EffectDisposition.Unknown, (await f.Effects.ReconcileAsync(effect, default)).Disposition);
        Assert.Equal(1, f.Launcher.Launches);
    }

    [Fact]
    public async Task Previous_message_send_attempt_cannot_project_into_the_current_attempt()
    {
        await using var f = new AdapterDeliveryFixture(); var message = f.Queue(); var effect = f.Claim(message);
        f.Db.Writer.Write("test.new-attempt", scope => scope.Execute("UPDATE engine_message SET attempt=attempt+1 WHERE id=$id", new SqlArg("$id", message)));
        Assert.Equal(EffectDisposition.Unknown, (await f.Effects.ExecuteAsync(effect, default)).Disposition);
        Assert.Equal(EffectDisposition.Unknown, (await f.Effects.ReconcileAsync(effect, default)).Disposition);
        Assert.Equal(0, f.Launcher.Launches);
    }

    [Fact]
    public async Task Release_without_a_captured_adapter_operation_never_fabricates_confirmation()
    {
        await using var f = new AdapterDeliveryFixture();
        f.Sessions.SetAcpSession(f.Session.SessionId, "retained-public-session");
        f.Db.Writer.Write("test.retained-native", scope => scope.Execute("UPDATE engine_session SET provider_session_id='retained-public-session' WHERE id=$id", new SqlArg("$id", f.Session.SessionId)));
        var result = f.Apply(new RequestHandoff(f.Session.SessionId, "workbox-test"), "handoff", f.Number("SELECT version FROM engine_session WHERE id=$id", f.Session.SessionId), new(Assembly.Engine.Contracts.ActorKind.Window, "owner")); Assert.True(result.Applied, result.Error);
        var id = f.Db.QueryFirst("SELECT id FROM engine_outbox WHERE kind='release'", row => row.Text(0), default)!;
        var effect = f.ClaimEffect(id);
        var executing = await f.Effects.ExecuteAsync(effect, default);
        Assert.Equal(EffectDisposition.Unknown, executing.Disposition);
        Assert.Equal("handoff_execution_unavailable", executing.Error);
        var reconciled = await f.Effects.ReconcileAsync(effect, default);
        Assert.Equal(EffectDisposition.Unknown, reconciled.Disposition);
        Assert.Equal("handoff_release_unconfirmed", reconciled.Error);
        Assert.Equal("handing_off", f.State("engine_session", f.Session.SessionId));
        Assert.Equal(0, f.Launcher.Launches);
    }

    [Fact]
    public async Task Frozen_v2_unknown_reservation_uses_original_id_and_raw_body_and_only_existing_ack_reconciles()
    {
        await using var f = new AdapterDeliveryFixture(legacy: true);
        var effect = f.ClaimEffect("send/uncertain");
        Assert.Equal("send/uncertain", effect.Payload.GetProperty("clientId").GetString());
        Assert.Equal("body uncertain", effect.Payload.GetProperty("text").GetString());
        var unknown = await f.Effects.ReconcileAsync(effect, default);
        Assert.Equal(EffectDisposition.Unknown, unknown.Disposition); Assert.Equal("delivery_unknown", unknown.Error);
        Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message WHERE id=$id", "uncertain"));
        var label = effect.Payload.GetProperty("senderLabel").GetString();
        f.Rows.Commit("uncertain", new RowConverter().Apply(new Assembly.Domain.Events.TurnStarted(2000, "original-run", "body uncertain", "send/uncertain", label)));
        f.Db.Writer.Write("test.existing-positive", scope =>
        {
            f.Rows.AcknowledgeUser(scope, "uncertain", "send/uncertain", new("prompt_inserted", "actual-historical-provider")); return 0;
        });
        var positive = await f.Effects.ReconcileAsync(effect, default);
        Assert.Equal(EffectDisposition.Confirmed, positive.Disposition); Assert.Equal("actual-historical-provider", Message(positive).ProviderTurnId);
        f.Time.Advance(TimeSpan.FromMinutes(31));
        Assert.True(effect.Attempt > f.Number("SELECT attempt FROM engine_message WHERE id=$id", "uncertain"));
        Assert.True(f.Complete(effect, positive).Applied);
        Assert.Equal("next_turn", f.State("engine_message", "uncertain")); Assert.Equal("done", f.State("engine_outbox", effect.Id));
        Assert.Equal(0, f.Launcher.Launches);
    }

    [Fact]
    public async Task Primitive_steering_rpc_failure_throws_without_prompt_fallback()
    {
        await using var f = new AdapterDeliveryFixture(); var first = f.Claim(f.Queue());
        var sending = f.Effects.ExecuteAsync(first, default); var agent = await f.Launcher.NextAsync(); await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt"); await agent.InsertAsync(prompt, "first-provider"); Assert.True(f.Complete(first, await sending.WaitAsync(DeliveryWait)).Applied);
        var steeringSend = f.Hosts.For(f.Session.SessionId).SteerAsync("legacy-steer", "legacy", null, default);
        var steering = await agent.ReadAsync("_session/steering"); await agent.ReplyErrorAsync(steering, -32603, "steer failed");
        var error = await Assert.ThrowsAsync<Assembly.Application.Common.Acp.JsonRpc.JsonRpcException>(() => steeringSend.WaitAsync(DeliveryWait));
        Assert.Equal("steer failed", error.Message); Assert.Single(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.Kind == "user");
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
    }

    [Fact]
    public async Task Owning_epoch_changed_during_slow_opener_is_unknown_before_any_prompt()
    {
        await using var f = new AdapterDeliveryFixture(); var effect = f.Claim(f.Queue());
        var sending = f.Effects.ExecuteAsync(effect, default); var agent = await f.Launcher.NextAsync();
        var initialize = await agent.ReadAsync("initialize"); await agent.ReplyAsync(initialize, """{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[]}""");
        var opening = await agent.ReadAsync("session/new");
        f.Db.Writer.Write("test.owner-change", scope => scope.Execute("UPDATE engine_session SET epoch=epoch+1 WHERE id=$id", new SqlArg("$id", f.Session.SessionId)));
        await agent.ReplyAsync(opening, """{"sessionId":"acp-1"}""");
        await agent.CloseAsync(); // Cleanup must settle before reporting the refused open; never prompt.
        var receipt = await sending.WaitAsync(DeliveryWait);
        Assert.True(receipt.Disposition == EffectDisposition.Unknown, JsonSerializer.Serialize(receipt, global::Assembly.Engine.EngineJson.Default.EffectReceipt));
        Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message WHERE id=$id", effect.Payload.GetProperty("messageId").GetString()!));
    }

    [Fact]
    public void Ordinary_message_effect_receipt_keeps_its_original_generated_bytes()
    {
        var original = new MessageEffectData("queued", null, "busy");
        Assert.Equal("{\"state\":\"queued\",\"providerTurnId\":null,\"reason\":\"busy\"}", JsonSerializer.Serialize(original, global::Assembly.Engine.EngineJson.Default.MessageEffectData));
        var withProof = original with { UserRow = new UserRowReference(1, "actual-run", 0) };
        Assert.Equal(withProof, JsonSerializer.Deserialize(JsonSerializer.Serialize(withProof, global::Assembly.Engine.EngineJson.Default.MessageEffectData), global::Assembly.Engine.EngineJson.Default.MessageEffectData));
    }
}
