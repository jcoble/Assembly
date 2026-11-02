using Assembly.Tests.Engine;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Events;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.Delivery;

public sealed partial class AdapterEffectsTests
{
    [Fact]
    public async Task Actual_start_verifies_projection_without_opening_an_adapter()
    {
        await using var f = new AdapterDeliveryFixture(worker: true);
        var started = f.Sessions.Create("claude", "https://github.com/other/repo");
        var id = f.Db.QueryFirst("SELECT id FROM engine_outbox WHERE kind='start' AND session_id=$id", r => r.Text(0), default, new SqlArg("$id", started.SessionId))!;
        await f.StateAsync("engine_outbox", id, "done");
        Assert.Equal("idle", f.State("engine_session", started.SessionId)); Assert.Equal(0, f.Launcher.Launches); Assert.Empty(f.Launcher.CheckedOut);
    }

    [Fact]
    public async Task Missing_start_projection_fails_and_never_opens_or_claims_resume()
    {
        await using var f = new AdapterDeliveryFixture();
        Assert.True(f.Apply(new StartSession("claude", null, "/existing", "Missing", null, null, null), "missing", 0, new(ActorKind.Window, "owner")).Applied);
        var id = f.Db.QueryFirst("SELECT id FROM engine_outbox WHERE kind='start' AND session_id='missing'", r => r.Text(0), default)!;
        var effect = f.ClaimEffect(id); var receipt = await f.Effects.ExecuteAsync(effect, default);
        Assert.Equal(EffectDisposition.Failed, receipt.Disposition); Assert.Equal("session_projection_missing", receipt.Error); Assert.Equal(0, f.Launcher.Launches);
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("foreign-chat")]
    [InlineData("foreign-owner")]
    [InlineData("ended")]
    public async Task Changed_owner_or_original_epoch_refuses_before_provider(string change)
    {
        await using var f = new AdapterDeliveryFixture(); var effect = f.Claim(f.Queue());
        f.Db.Writer.Write("test.owner-change", s => s.Execute(change switch
        {
            "epoch" => "UPDATE engine_session SET epoch=epoch+1 WHERE id=$id",
            "foreign-chat" => "UPDATE session_info SET machine='foreign' WHERE session_id=$id",
            "foreign-owner" => "UPDATE engine_session SET holder='foreign',state='foreign' WHERE id=$id",
            _ => "UPDATE engine_session SET state='ended' WHERE id=$id"
        }, new SqlArg("$id", f.Session.SessionId)));
        Assert.Equal(EffectDisposition.Unknown, (await f.Effects.ExecuteAsync(effect, default)).Disposition); Assert.Equal(0, f.Launcher.Launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Old_unacknowledged_row_or_foreign_copy_never_authorizes_delivery(bool foreignCopy)
    {
        await using var f = new AdapterDeliveryFixture(); var effect = f.Claim(f.Queue());
        var rowSession = foreignCopy ? "foreign-session" : f.Session.SessionId;
        f.Rows.Commit(rowSession, new RowConverter().Apply(new TurnStarted(1, "legacy-transcript-run", "exact body", "C")));
        if (foreignCopy)
        {
            // No owning row exists: a foreign copy is not evidence for this recipient's provider insertion.
            Assert.Equal(EffectDisposition.Unknown, (await f.Effects.ReconcileAsync(effect, default)).Disposition);
        }
        else
        {
            Assert.Equal(EffectDisposition.Unknown, (await f.Effects.ExecuteAsync(effect, default)).Disposition);
            Assert.Equal(EffectDisposition.Unknown, (await f.Effects.ReconcileAsync(effect, default)).Disposition);
        }
        Assert.Equal(0, f.Launcher.Launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Auth_proof_cannot_reuse_pruned_recreated_or_ambiguous_user_row(bool ambiguous)
    {
        await using var f = new AdapterDeliveryFixture(); var id = f.Queue(); var effect = f.Claim(id);
        var sending = f.Effects.ExecuteAsync(effect, default); var agent = await f.Launcher.NextAsync(); await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt"); await agent.ReplyErrorAsync(prompt, -32000, "sign in");
        var refused = await sending.WaitAsync(DeliveryWait); var original = Message(refused).UserRow!; Assert.True(f.Complete(effect, refused).Applied); await agent.CloseAsync();
        if (!ambiguous) f.Db.Writer.Write("test.prune", s => s.Execute("DELETE FROM rows WHERE session_id=$id AND seq=$seq", new SqlArg("$id", f.Session.SessionId), new("$seq", original.Seq)));
        f.Rows.Commit(f.Session.SessionId, new RowConverter().Apply(new TurnStarted(2000, original.RunId, "exact body", "C")));
        Assert.True(f.Apply(new SetSessionState("lost", null, "test"), f.Session.SessionId, f.Number("SELECT version FROM engine_session WHERE id=$id", f.Session.SessionId)).Applied);
        Assert.True(f.Apply(new SetSessionState("idle", null, null), f.Session.SessionId, f.Number("SELECT version FROM engine_session WHERE id=$id", f.Session.SessionId)).Applied);
        var retry = f.Claim(id);
        Assert.Equal(EffectDisposition.Unknown, (await f.Effects.ExecuteAsync(retry, default)).Disposition);
        Assert.Equal(1, f.Launcher.Launches);
    }

    [Fact]
    public async Task Busy_without_a_row_then_later_same_client_row_is_unknown_not_reuse_permission()
    {
        await using var f = new AdapterDeliveryFixture(); var id = f.Queue("race body", "race"); var first = f.Claim(id);
        var host = f.Hosts.For(f.Session.SessionId); var other = host.SendAsync("other", "other", null, default);
        var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "other-provider"); Assert.IsType<SendResult.Inserted>(await other.WaitAsync(DeliveryWait));
        var busy = await f.Effects.ExecuteAsync(first, default); Assert.Equal("busy", Message(busy).Reason); Assert.Null(Message(busy).UserRow); Assert.True(f.Complete(first, busy).Applied);
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
        var late = host.SendAsync("race body", "race", null, default); await agent.OpenAsync(); var actual = await agent.ReadAsync("session/prompt");
        var retry = f.Claim(id); Assert.Equal(EffectDisposition.Unknown, (await f.Effects.ExecuteAsync(retry, default)).Disposition);
        Assert.False(late.IsCompleted); // The only provider prompt is this independently owned legacy operation.
        await agent.InsertAsync(actual, "legacy-provider"); await late.WaitAsync(DeliveryWait);
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
    }

    [Fact]
    public async Task Positive_response_cannot_ack_a_recreated_same_client_row()
    {
        await using var f = new AdapterDeliveryFixture(); var effect = f.Claim(f.Queue());
        var sending = f.Effects.ExecuteAsync(effect, default); var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "original-provider"); var receipt = await sending.WaitAsync(DeliveryWait); var original = Message(receipt).UserRow!;
        f.Db.Writer.Write("test.prune", s => s.Execute("DELETE FROM rows WHERE session_id=$id AND seq=$seq", new SqlArg("$id", f.Session.SessionId), new("$seq", original.Seq)));
        f.Rows.Commit(f.Session.SessionId, new RowConverter().Apply(new TurnStarted(2000, original.RunId, "exact body", "C")));
        Assert.Equal("delivery_ack_conflict", Assert.Throws<Assembly.Application.Common.Calls.CallFailed>(() => f.Complete(effect, receipt)).Message);
        Assert.Equal("inflight", f.State("engine_outbox", effect.Id)); Assert.Equal("queued", f.State("engine_message", effect.Payload.GetProperty("messageId").GetString()!));
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
    }
}
