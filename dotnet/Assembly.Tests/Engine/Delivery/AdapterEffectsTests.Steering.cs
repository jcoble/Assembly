using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Domain.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Engine.Delivery;

// A message sent to a running Claude turn by the prompt-queueing route, executed by the engine: it waits on the agent
// without holding up anything else on its session, and is never sent twice.
public sealed partial class AdapterEffectsTests
{
    private const string SteerText = "steer text";
    private const string SteerClient = "steer-client";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_runs_while_a_steer_waits(bool insertionWins)
    {
        await using var f = new AdapterDeliveryFixture(worker: true);
        f.Queue("first", "first"); var agent = await f.Launcher.NextAsync();
        try
        {
            var queued = await QueuedSteerAsync(f, agent);
            var steer = OwnerPromptIdentity.MessageId(f.Session.SessionId, SteerClient);
            Assert.True(f.Apply(new StopSession(), f.Session.SessionId, f.Number("SELECT version FROM engine_session WHERE id=$id", f.Session.SessionId),
                new(ActorKind.Window, "owner")).Applied);
            var stopId = f.Db.QueryFirst("SELECT id FROM engine_outbox WHERE kind='stop' AND session_id=$id", r => r.Text(0), default, new SqlArg("$id", f.Session.SessionId))!;
            await agent.ReadAsync("session/cancel");
            if (insertionWins) await agent.InsertAsync(queued, "steer-provider");
            else await agent.ReplyErrorAsync(queued, -32800, "cancelled");
            await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"cancelled"}""");
            await agent.CloseAsync();
            await f.StateAsync("engine_outbox", stopId, "done");
            if (insertionWins)
            {
                await f.StateAsync("engine_message", steer, "delivered_to_turn");
            }
            else
            {
                // Refused before it was taken: it runs once as the next turn.
                await agent.OpenAsync();
                var again = await agent.ReadAsync("session/prompt");
                Assert.Equal(SteerText, PromptText(again));
                await agent.FinishAsync(again, """{"stopReason":"end_turn"}""");
                await agent.CloseAsync();
                await f.StateAsync("engine_message", steer, "next_turn");
            }
            Assert.Single(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.Kind == "user" && row.Payload.GetProperty("text").GetString() == SteerText);
            Assert.Equal(insertionWins ? 1 : 2, SendEffects(f, steer));
            Assert.Equal(1, f.Launcher.Launches);
        }
        finally { agent.Crash(); }
    }

    [Fact]
    public async Task An_answer_runs_while_a_steer_waits()
    {
        await using var f = new AdapterDeliveryFixture(worker: true);
        f.Queue("first", "first"); var agent = await f.Launcher.NextAsync();
        try
        {
            var queued = await QueuedSteerAsync(f, agent);
            var steer = OwnerPromptIdentity.MessageId(f.Session.SessionId, SteerClient);
            using var live = f.Hub.Subscribe(f.Session.SessionId, f.Rows.LastSeq(f.Session.SessionId));
            await agent.RequestAsync(7, "session/request_permission", PermissionRequest.ReplaceLineEndings(""));
            var need = (await AskRow(live, "open")).ItemId![4..];
            var answer = JsonSerializer.SerializeToElement(new { optionId = "yes" });
            var prepared = await OwnerNeedService(f).PrepareOwnerAsync(OwnerWindow(f, need, answer), default);
            Assert.True(f.Db.Writer.Write("test.prepared-answer-while-steering", prepared).Applied);
            Assert.Contains("yes", (await agent.ResultAsync()).GetRawText());
            await f.StateAsync("engine_outbox", "answer/" + need, "done");
            Assert.Equal("queued", f.State("engine_message", steer));
            await agent.InsertAsync(queued, "steer-provider");
            await f.StateAsync("engine_message", steer, "delivered_to_turn");
            await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
            await agent.CloseAsync();
        }
        finally { agent.Crash(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Steer_receipt_follows_its_row_in_either_pipe_order(bool replyFirst)
    {
        await using var f = new AdapterDeliveryFixture();
        var (agent, effect, steering) = await ExecutingSteerAsync(f);
        var queued = await agent.ReadAsync("session/prompt");
        var echo = JsonSerializer.Serialize(new { sessionUpdate = "user_message", messageId = "steer-provider", content = Array.Empty<object>() });
        if (replyFirst) await agent.ReplyThenUpdateAsync(queued, """{"messageId":"steer-provider"}""", echo);
        else
        {
            await agent.UpdateAsync(echo);
            await agent.ReplyAsync(queued, """{"messageId":"steer-provider"}""");
        }
        var receipt = await steering.WaitAsync(DeliveryWait);
        var row = Assert.Single(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.Kind == "user" && row.Payload.GetProperty("text").GetString() == SteerText);
        Assert.Equal("delivered_to_turn", Message(receipt).State);
        Assert.Equal(row.Seq, Message(receipt).UserRow!.Seq);
        using (var snapshot = f.Db.OpenSnapshot(default))
            Assert.Equal(new UserAcknowledgment("steer_injected", null), f.Rows.UserDeliveryOfClient(snapshot, f.Session.SessionId, SteerClient)!.Acknowledgment);
        Assert.True(f.Complete(effect, receipt).Applied);
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recovery_reconciles_an_uncertain_steer_without_sending_it_again(bool inserted)
    {
        await using var f = new AdapterDeliveryFixture();
        using var cancelling = new CancellationTokenSource();
        var (agent, effect, steering) = await ExecutingSteerAsync(f, cancelling);
        var steer = OwnerPromptIdentity.MessageId(f.Session.SessionId, SteerClient);
        var queued = await agent.ReadAsync("session/prompt");
        // The engine stops waiting; the request on the wire is kept, and the agent settles it later.
        await cancelling.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => steering.WaitAsync(DeliveryWait));
        if (inserted) await agent.InsertAsync(queued, "late-steer-provider");
        else await agent.ReplyErrorAsync(queued, -32800, "cancelled");
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
        await agent.CloseAsync();
        using (var snapshot = f.Db.OpenSnapshot(default))
            Assert.Equal(inserted ? new UserAcknowledgment("steer_injected", null) : null,
                f.Rows.UserDeliveryOfClient(snapshot, f.Session.SessionId, SteerClient)?.Acknowledgment);
        await f.RestartAsync();
        await f.StateAsync("engine_outbox", effect.Id, "uncertain");
        var claimed = f.Apply(new ClaimEffect(), effect.Id, f.Number("SELECT version FROM engine_outbox WHERE id=$id", effect.Id)); Assert.True(claimed.Applied, claimed.Error);
        var reconciled = await f.Effects.ReconcileAsync(EffectTransitions.Read(f.Db, effect.Id, default)!, default);
        if (inserted) Assert.Equal("delivered_to_turn", Message(reconciled).State);
        else Assert.Equal(EffectDisposition.Unknown, reconciled.Disposition);
        Assert.Equal(1, SendEffects(f, steer));
        Assert.Equal(1, f.Launcher.Launches);
    }

    [Fact]
    public async Task Closing_with_a_waiting_steer_runs_and_never_resends()
    {
        await using var f = new AdapterDeliveryFixture();
        var (agent, effect, steering) = await ExecutingSteerAsync(f);
        var steer = OwnerPromptIdentity.MessageId(f.Session.SessionId, SteerClient);
        await agent.ReadAsync("session/prompt");
        var turn = f.Hosts.For(f.Session.SessionId).CurrentTurn.Done;
        // The turn ends with the message still waiting, so the session is kept for it; the host then shuts down. Older
        // events are skipped, so the wait below is for this turn's end.
        while (f.Events.Reader.TryRead(out _)) { }
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
        await f.StateAsync("engine_session", f.Session.SessionId, "idle");
        await turn.WaitAsync(DeliveryWait); // Engine idle is published before the turn finishes saving its end and decides to keep the session.
        var restarting = f.RestartAsync();
        await agent.CloseAsync();
        var receipt = await steering.WaitAsync(DeliveryWait);
        await restarting.WaitAsync(DeliveryWait);
        Assert.Equal(EffectDisposition.Unknown, receipt.Disposition);
        Assert.DoesNotContain(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.Kind == "user" && row.Payload.GetProperty("text").GetString() == SteerText);
        await f.StateAsync("engine_outbox", effect.Id, "uncertain");
        var claimed = f.Apply(new ClaimEffect(), effect.Id, f.Number("SELECT version FROM engine_outbox WHERE id=$id", effect.Id)); Assert.True(claimed.Applied, claimed.Error);
        Assert.Equal(EffectDisposition.Unknown, (await f.Effects.ReconcileAsync(EffectTransitions.Read(f.Db, effect.Id, default)!, default)).Disposition);
        Assert.Equal(1, SendEffects(f, steer));
        Assert.Equal(1, f.Launcher.Launches);
    }

    // With the first message queued, the worker sends it to a prompt-queueing agent, then releases a second message into that turn as a
    // steer; returns the agent's read of that steer's session/prompt.
    private static async Task<JsonElement> QueuedSteerAsync(AdapterDeliveryFixture f, FakeLink agent)
    {
        var first = OwnerPromptIdentity.MessageId(f.Session.SessionId, "first");
        await agent.StartAsync(canSteer: false, promptQueueing: true);
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "first-provider");
        await f.StateAsync("engine_message", first, "next_turn");
        await f.StateAsync("engine_session", f.Session.SessionId, "running");
        f.Queue(SteerText, SteerClient);
        var queued = await agent.ReadAsync("session/prompt");
        Assert.Equal(SteerText, PromptText(queued));
        return queued;
    }

    // The same by hand: the steer's effect is claimed and executing when this returns.
    private static async Task<(FakeLink Agent, Effect Effect, Task<EffectReceipt> Steering)> ExecutingSteerAsync(AdapterDeliveryFixture f, CancellationTokenSource? cancelling = null)
    {
        var first = f.Claim(f.Queue("first", "first")); var sending = f.Effects.ExecuteAsync(first, default);
        var agent = await f.Launcher.NextAsync(); await agent.StartAsync(canSteer: false, promptQueueing: true);
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "first-provider"); Assert.True(f.Complete(first, await sending.WaitAsync(DeliveryWait)).Applied);
        await f.StateAsync("engine_session", f.Session.SessionId, "running");
        var effect = f.Claim(f.Queue(SteerText, SteerClient));
        Assert.Equal("steer", effect.Payload.GetProperty("how").GetString());
        return (agent, effect, f.Effects.ExecuteAsync(effect, cancelling?.Token ?? default));
    }

    private static string? PromptText(JsonElement prompt) => prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString();

    private static long SendEffects(AdapterDeliveryFixture f, string message) =>
        f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='send' AND json_extract(payload_json,'$.messageId')=$id", message);
}
