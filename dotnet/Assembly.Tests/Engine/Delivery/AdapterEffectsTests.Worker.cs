using Assembly.Tests.Engine;
using Assembly.Infrastructure.Engine;
using Assembly.Application.Common.Engine;
using Assembly.Tests.Features.Chat.Sessions;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.Delivery;

public sealed partial class AdapterEffectsTests
{
    [Fact]
    public async Task Real_worker_slow_stop_does_not_delay_other_session_provider_answer()
    {
        await using var f = new AdapterDeliveryFixture(worker: true);
        var first = f.Queue("held", "first"); var agent = await f.Launcher.NextAsync(); FakeLink? other = null;
        try
        {
            await agent.StartAsync();
            var prompt = await agent.ReadAsync("session/prompt"); await agent.InsertAsync(prompt, "first-provider"); await f.StateAsync("engine_message", first, "next_turn");
            Assert.True(f.Apply(new StopSession(), f.Session.SessionId, f.Number("SELECT version FROM engine_session WHERE id=$id", f.Session.SessionId), new(ActorKind.Window, "owner")).Applied);
            var stopId = f.Db.QueryFirst("SELECT id FROM engine_outbox WHERE kind='stop' AND session_id=$id", r => r.Text(0), default, new SqlArg("$id", f.Session.SessionId))!;
            await agent.ReadAsync("session/cancel"); Assert.Equal("inflight", f.State("engine_outbox", stopId));
            var second = f.Sessions.CreateReady("codex", "repo");
            var secondMessage = f.Queue("other session", "second", sessionId: second.SessionId);
            other = await f.Launcher.NextAsync(); await other.StartAsync(); var otherPrompt = await other.ReadAsync("session/prompt");
            await other.InsertAsync(otherPrompt, "second-provider"); await f.StateAsync("engine_message", secondMessage, "next_turn");
            using var live = f.Hub.Subscribe(second.SessionId, f.Rows.LastSeq(second.SessionId));
            await other.RequestAsync(7, "session/request_permission", PermissionRequest.ReplaceLineEndings(""));
            var asked = await AskRow(live, "open"); var need = asked.ItemId![4..];
            var answer = JsonSerializer.SerializeToElement(new { optionId = "yes" });
            var window = new WindowCommand(Guid.NewGuid(), need, 1,
                f.Number("SELECT epoch FROM engine_session WHERE id=$id", second.SessionId), new ResolveNeed("answered", answer));
            var prepared = await OwnerNeedService(f).PrepareOwnerAsync(window, default);
            Assert.True(f.Db.Writer.Write("test.prepared-other-session-answer", prepared).Applied);
            Assert.Contains("yes", (await other.ResultAsync()).GetRawText());
            await f.StateAsync("engine_outbox", "answer/" + need, "done");
            Assert.Equal("inflight", f.State("engine_outbox", stopId));
            var effect = EffectTransitions.Read(f.Db, "answer/" + need, default)!;
            Assert.Equal(EffectDisposition.Confirmed, (await f.Effects.ReconcileAsync(effect, default)).Disposition);
            await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"cancelled"}"""); await agent.CloseAsync();
            await f.StateAsync("engine_outbox", stopId, "done");
            await other.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await other.CloseAsync();
        }
        finally { other?.Crash(); agent.Crash(); }
    }

    [Fact]
    public async Task Answer_checks_actual_waiting_provider_identity_without_answering_another_ask()
    {
        await using var f = new AdapterDeliveryFixture(); var first = f.Claim(f.Queue()); var sending = f.Effects.ExecuteAsync(first, default);
        var agent = await f.Launcher.NextAsync();
        try
        {
            await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt"); await agent.InsertAsync(prompt, "provider"); Assert.True(f.Complete(first, await sending.WaitAsync(DeliveryWait)).Applied);
            using var live = f.Hub.Subscribe(f.Session.SessionId, f.Rows.LastSeq(f.Session.SessionId));
            await agent.RequestAsync(7, "session/request_permission", PermissionRequest.ReplaceLineEndings(""));
            var asked = await AskRow(live, "open"); var actualNeed = asked.ItemId![4..];
            await agent.RequestAsync(8, "session/request_permission", PermissionRequest.ReplaceLineEndings(""));
            var otherAsk = await AskRow(live, "open"); var wrongNeed = otherAsk.ItemId![4..];
            var answer = JsonSerializer.SerializeToElement(new { optionId = "yes" });
            var host = f.Hosts.For(f.Session.SessionId); var captured = host.PrepareProviderAnswer(wrongNeed, answer)!;
            var wrongPreparation = await OwnerNeedService(f).PrepareOwnerAsync(OwnerWindow(f, wrongNeed, answer), default);
            Assert.True(f.Db.Writer.Write("test.prepared-ended-request-answer", wrongPreparation).Applied);
            var observed = captured.Binding.Observation;
            Assert.True(host.CancelProviderRequest(captured.Binding.ProviderRequestId,
                new(captured.Binding.SessionId, captured.Binding.ChatRowId, observed.Epoch, observed.RunId, observed.StepId, observed.Attempt, "cancelled")));
            Assert.Equal("cancelled", (await agent.ResultAsync()).GetProperty("outcome").GetProperty("outcome").GetString());
            await AskRow(live, "cancelled");
            var wrong = f.ClaimEffect("answer/" + wrongNeed); var refused = await f.Effects.ExecuteAsync(wrong, default);
            Assert.Equal(EffectDisposition.Failed, refused.Disposition); Assert.Equal("request_gone", refused.Error); Assert.True(f.Complete(wrong, refused).Applied);
            Assert.Equal("open", f.State("engine_needs_me", actualNeed));
            var exactPreparation = await OwnerNeedService(f).PrepareOwnerAsync(OwnerWindow(f, actualNeed, answer), default);
            Assert.True(f.Db.Writer.Write("test.prepared-exact-request-answer", exactPreparation).Applied);
            var exact = f.ClaimEffect("answer/" + actualNeed); var accepted = await f.Effects.ExecuteAsync(exact, default); Assert.Equal(EffectDisposition.Confirmed, accepted.Disposition);
            Assert.Contains("yes", (await agent.ResultAsync()).GetRawText()); Assert.True(f.Complete(exact, accepted).Applied);
            await AskRow(live, "answered"); Assert.Equal(EffectDisposition.Confirmed, (await f.Effects.ReconcileAsync(exact, default)).Disposition);
            await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
        }
        finally { agent.Crash(); }
    }

    [Theory]
    [InlineData("ack")]
    [InlineData("message")]
    [InlineData("receipt")]
    public async Task Real_worker_failed_completion_stays_unknown_through_restart_with_one_provider_prompt(string failure)
    {
        await using var f = new AdapterDeliveryFixture(worker: true);
        f.Db.Writer.Write("test.block-complete", s => s.Execute(failure switch
        {
            "ack" => "CREATE TRIGGER block_completion BEFORE UPDATE OF payload ON rows BEGIN SELECT RAISE(ABORT,'ack blocked'); END",
            "message" => "CREATE TRIGGER block_completion BEFORE UPDATE OF state ON engine_message WHEN NEW.state='next_turn' BEGIN SELECT RAISE(ABORT,'message blocked'); END",
            _ => "CREATE TRIGGER block_completion BEFORE UPDATE OF receipt_json ON engine_outbox WHEN json_extract(NEW.receipt_json,'$.disposition')=0 BEGIN SELECT RAISE(ABORT,'receipt blocked'); END"
        }));
        var message = f.Queue(); var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "externally-accepted");
        var effectId = f.Db.QueryFirst("SELECT id FROM engine_outbox WHERE kind='send' AND json_extract(payload_json,'$.messageId')=$id", r => r.Text(0), default, new SqlArg("$id", message))!;
        await f.StateAsync("engine_outbox", effectId, "uncertain");
        Assert.Equal("queued", f.State("engine_message", message)); Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message WHERE id=$id", message));
        using (var snapshot = f.Db.OpenSnapshot(default)) Assert.Null(f.Rows.UserDeliveryOfClient(snapshot, f.Session.SessionId, "C")!.Acknowledgment);
        f.Db.Writer.Write("test.unblock", s => s.Execute("DROP TRIGGER block_completion"));
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
        var version = f.Number("SELECT version FROM engine_outbox WHERE id=$id", effectId);
        await f.RestartAsync(); f.Time.Advance(TimeSpan.FromSeconds(1));
        using var timeout = new CancellationTokenSource(DeliveryWait);
        while (true)
        {
            var e = await f.Events.Reader.ReadAsync(timeout.Token);
            if (e.EntityId == effectId && e.Data.State == "uncertain" && e.Version > version) break;
        }
        Assert.Equal(1, f.Launcher.Launches); Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message WHERE id=$id", message));
        Assert.Equal(2, f.Number("SELECT attempt FROM engine_outbox WHERE id=$id", effectId));
    }

    private const string PermissionRequest = """
        {"sessionId":"acp-1","title":"Permission","subject":{"type":"tool_call","toolCall":{"toolCallId":"t1","title":"Permission"}},
         "options":[{"optionId":"yes","name":"Allow","kind":"allow_once"},{"optionId":"no","name":"Reject","kind":"reject_once"}]}
        """;
    private static async Task<StoredRow> AskRow(LiveSubscription live, string state)
    {
        using var timeout = new CancellationTokenSource(DeliveryWait);
        while (true) { var row = await live.Rows.ReadAsync(timeout.Token); if (row.Kind == "ask" && row.Payload.GetProperty("state").GetString() == state) return row; }
    }
}
