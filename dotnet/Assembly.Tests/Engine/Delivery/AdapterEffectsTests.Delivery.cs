using Assembly.Tests.Engine;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Delivery;

public sealed partial class AdapterEffectsTests
{
    private static readonly TimeSpan DeliveryWait = TimeSpan.FromSeconds(8);
    private static MessageEffectData Message(EffectReceipt receipt) => receipt.Data.Deserialize(global::Assembly.Engine.EngineJson.Default.MessageEffectData)!;

    [Fact]
    public async Task Prompt_insertion_persists_real_ack_and_reconciles_without_resend()
    {
        await using var f = new AdapterDeliveryFixture();
        var text = "raw\0 body [message from text] 😀";
        var id = f.Queue(text, "owner-client", refs: ["first.png", "second.png"]);
        var effect = f.Claim(id); var delivery = effect.Payload.Deserialize(global::Assembly.Engine.EngineJson.Default.DeliverMessage)!;
        Assert.NotEqual(id, delivery.ClientId); Assert.Equal(["first.png", "second.png"], delivery.AttachmentIds);
        var executing = f.Effects.ExecuteAsync(effect, default);
        var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        Assert.Equal(text, prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.False(executing.IsCompleted);
        var original = Assert.Single(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.Kind == "user");
        Assert.False(original.Payload.TryGetProperty("from", out _)); Assert.Equal("owner-client", original.Payload.GetProperty("clientId").GetString());
        await agent.InsertAsync(prompt, "actual-provider-id");
        var receipt = await executing.WaitAsync(DeliveryWait);
        Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition); Assert.Equal("next_turn", Message(receipt).State);
        Assert.Equal("actual-provider-id", Message(receipt).ProviderTurnId); Assert.NotNull(Message(receipt).UserRow);
        Assert.True(f.Complete(effect, receipt).Applied);
        using (var snapshot = f.Db.OpenSnapshot(default))
            Assert.Equal(new UserAcknowledgment("prompt_inserted", "actual-provider-id"), f.Rows.UserDeliveryOfClient(snapshot, f.Session.SessionId, "owner-client")!.Acknowledgment);
        var reconciled = await f.Effects.ReconcileAsync(effect, default);
        Assert.Equal("actual-provider-id", Message(reconciled).ProviderTurnId);
        Assert.Single(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.Kind == "user");
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
        await f.RestartAsync();
        Assert.Equal("actual-provider-id", Message(await f.Effects.ReconcileAsync(effect, default)).ProviderTurnId);
        Assert.Equal(1, f.Launcher.Launches);
    }

    [Fact]
    public async Task Actual_positive_provider_insertion_wins_after_deadline_with_atomic_ack()
    {
        await using var f = new AdapterDeliveryFixture(); var message = f.Queue("late accepted", "late", refs: ["original.png"]); var effect = f.Claim(message);
        var sending = f.Effects.ExecuteAsync(effect, default); var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "actual-late-provider"); var receipt = await sending.WaitAsync(DeliveryWait);
        f.Time.Advance(TimeSpan.FromMinutes(31)); Assert.True(f.Complete(effect, receipt).Applied);
        Assert.Equal("next_turn", f.State("engine_message", message));
        using (var scope = f.Db.OpenSnapshot(default)) Assert.Equal("actual-late-provider", f.Rows.UserDeliveryOfClient(scope, f.Session.SessionId, "late")!.Acknowledgment!.ProviderMessageId);
        Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_event WHERE entity_id=$id AND json_extract(data_json,'$.state')='failed'", r => r.Long(0), default, new SqlArg("$id", message)));
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
    }

    [Theory]
    [InlineData("injected", "delivered_to_turn")]
    [InlineData("promptRequired", "queued")]
    public async Task Steering_reports_actual_outcome_and_never_falls_back_to_prompt(string outcome, string state)
    {
        await using var f = new AdapterDeliveryFixture();
        var first = f.Claim(f.Queue("first", "first")); var sending = f.Effects.ExecuteAsync(first, default);
        var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "first-provider"); Assert.True(f.Complete(first, await sending.WaitAsync(DeliveryWait)).Applied);
        var id = f.Queue("steer text", "steer-client"); var effect = f.Claim(id);
        Assert.Equal("steer", effect.Payload.GetProperty("how").GetString()); Assert.Equal("steer-client", effect.Payload.GetProperty("clientId").GetString());
        var steering = f.Effects.ExecuteAsync(effect, default); var request = await agent.ReadAsync("_session/steering");
        await agent.ReplyAsync(request, $$"""{"outcome":"{{outcome}}"}"""); var receipt = await steering.WaitAsync(DeliveryWait);
        Assert.Equal(state, Message(receipt).State); Assert.Null(Message(receipt).ProviderTurnId); Assert.True(f.Complete(effect, receipt).Applied);
        var users = f.Rows.RowsAfter(f.Session.SessionId, 0).Where(row => row.Kind == "user").ToArray();
        Assert.Equal(outcome == "injected" ? 2 : 1, users.Length);
        if (outcome == "injected")
        {
            using var snapshot = f.Db.OpenSnapshot(default);
            Assert.Equal(new UserAcknowledgment("steer_injected", null), f.Rows.UserDeliveryOfClient(snapshot, f.Session.SessionId, "steer-client")!.Acknowledgment);
            Assert.Equal("delivered_to_turn", Message(await f.Effects.ReconcileAsync(effect, default)).State);
        }
        else Assert.Equal("prompt_required", Message(receipt).Reason);
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
        Assert.Equal(1, f.Launcher.Launches);
    }

    [Fact]
    public async Task Auth_refusal_reuses_only_its_original_row_on_checked_retry_after_restart()
    {
        await using var f = new AdapterDeliveryFixture(); var id = f.Queue("retry same body"); var effect = f.Claim(id);
        var sending = f.Effects.ExecuteAsync(effect, default); var agent = await f.Launcher.NextAsync(); await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt"); await agent.ReplyErrorAsync(prompt, -32000, "sign in first");
        var refused = await sending.WaitAsync(DeliveryWait); Assert.Equal("auth_required", Message(refused).Reason);
        var original = Message(refused).UserRow!; Assert.NotNull(original); Assert.True(f.Complete(effect, refused).Applied);
        await agent.CloseAsync(); await f.RestartAsync();
        var lost = f.Apply(new SetSessionState("lost", null, "test.auth-corrected"), f.Session.SessionId, f.Number("SELECT version FROM engine_session WHERE id=$id", f.Session.SessionId)); Assert.True(lost.Applied, lost.Error);
        var idle = f.Apply(new SetSessionState("idle", null, null), f.Session.SessionId, f.Number("SELECT version FROM engine_session WHERE id=$id", f.Session.SessionId)); Assert.True(idle.Applied, idle.Error);
        var retry = f.Claim(id); var resending = f.Effects.ExecuteAsync(retry, default);
        var nextAgent = await f.Launcher.NextAsync(); await nextAgent.StartAsync(); var actual = await nextAgent.ReadAsync("session/prompt");
        await nextAgent.InsertAsync(actual, "only-inserted-prompt"); var accepted = await resending.WaitAsync(DeliveryWait);
        Assert.Equal(original, Message(accepted).UserRow); Assert.True(f.Complete(retry, accepted).Applied);
        Assert.Single(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.Kind == "user");
        await nextAgent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await nextAgent.CloseAsync();
    }

    [Theory]
    [InlineData("ack")]
    [InlineData("message")]
    [InlineData("receipt")]
    public async Task Provider_acceptance_then_local_failure_rolls_back_all_completion_and_restart_never_resends(string failure)
    {
        await using var f = new AdapterDeliveryFixture(); var id = f.Queue(); var effect = f.Claim(id);
        var sending = f.Effects.ExecuteAsync(effect, default); var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "externally-accepted"); var receipt = await sending.WaitAsync(DeliveryWait);
        await f.StateAsync("engine_session", f.Session.SessionId, "running");
        var counts = f.Db.QueryFirst("SELECT (SELECT COUNT(*) FROM engine_event),(SELECT COUNT(*) FROM engine_command_receipt)", r => (r.Long(0), r.Long(1)), default);
        var sql = failure switch
        {
            "ack" => "CREATE TRIGGER block_completion BEFORE UPDATE OF payload ON rows BEGIN SELECT RAISE(ABORT,'ack blocked'); END",
            "message" => "CREATE TRIGGER block_completion BEFORE UPDATE OF state ON engine_message BEGIN SELECT RAISE(ABORT,'message blocked'); END",
            _ => "CREATE TRIGGER block_completion BEFORE INSERT ON engine_command_receipt BEGIN SELECT RAISE(ABORT,'receipt blocked'); END"
        };
        f.Db.Writer.Write("test.block", s => s.Execute(sql)); Assert.Throws<SqliteException>(() => f.Complete(effect, receipt));
        Assert.Equal("inflight", f.State("engine_outbox", effect.Id)); Assert.Equal("queued", f.State("engine_message", id)); Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message WHERE id=$id", id));
        Assert.Equal(counts, f.Db.QueryFirst("SELECT (SELECT COUNT(*) FROM engine_event),(SELECT COUNT(*) FROM engine_command_receipt)", r => (r.Long(0), r.Long(1)), default));
        using (var snapshot = f.Db.OpenSnapshot(default)) Assert.Null(f.Rows.UserDeliveryOfClient(snapshot, f.Session.SessionId, "C")!.Acknowledgment);
        f.Db.Writer.Write("test.unblock", s => s.Execute("DROP TRIGGER block_completion"));
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
        await f.RestartAsync();
        await f.StateAsync("engine_outbox", effect.Id, "uncertain");
        var claimed = f.Apply(new ClaimEffect(), effect.Id, f.Number("SELECT version FROM engine_outbox WHERE id=$id", effect.Id)); Assert.True(claimed.Applied, claimed.Error);
        var recovered = EffectTransitions.Read(f.Db, effect.Id, default)!;
        Assert.Equal(EffectDisposition.Unknown, (await f.Effects.ReconcileAsync(recovered, default)).Disposition);
        Assert.Equal(1, f.Launcher.Launches); Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message WHERE id=$id", id));
    }
}
