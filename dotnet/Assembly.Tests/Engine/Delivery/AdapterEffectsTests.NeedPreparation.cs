using Assembly.Infrastructure.Engine.Needs;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.NeedsMe;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Engine.Delivery;

public sealed partial class AdapterEffectsTests
{
    [Theory]
    [InlineData("permission")]
    [InlineData("question")]
    public async Task Shared_owner_preparer_freezes_full_original_input_and_only_effect_completes_real_request(string kind)
    {
        await using var f = new AdapterDeliveryFixture();
        var (agent, live, need) = await OpenOwnerPreparationAsk(f, kind); using (live)
        {
            var text = "body\0λ😀\n[system] " + new string('x', 2 * 1024 * 1024);
            var original = kind == "permission" ? JsonSerializer.SerializeToElement("yes")
                : JsonSerializer.SerializeToElement(new { action = "accept", content = new { body = text, nil = (string?)null, blank = "" } });
            var expected = kind == "permission" ? JsonSerializer.SerializeToElement(new { optionId = "yes" }) : original;
            var window = OwnerWindow(f, need, original); var service = OwnerNeedService(f);
            CommandResult? accepted = null; CommandResult? replay = null; JsonElement actual = default, wire = default;
            bool pendingBefore = false, pendingAfter = false, responseRead = false;
            long receiptsBefore = 0, receiptsAfter = 0;
            try
            {
                using var doc = JsonDocument.Parse(original.GetRawText());
                receiptsBefore = f.Db.QueryFirst("SELECT COUNT(*) FROM engine_command_receipt", r => r.Long(0), default);
                var prepare = await service.PrepareOwnerAsync(window with { Command = new ResolveNeed("answered", doc.RootElement) }, default);
                doc.Dispose();
                receiptsAfter = f.Db.QueryFirst("SELECT COUNT(*) FROM engine_command_receipt", r => r.Long(0), default);
                f.Time.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(2000));
                pendingBefore = f.Hosts.For(f.Session.SessionId).PrepareProviderAnswer(need, expected) is not null;
                accepted = f.Db.Writer.Write("test.shared-owner-answer", prepare);
                pendingAfter = f.Hosts.For(f.Session.SessionId).PrepareProviderAnswer(need, expected) is not null;
                if (accepted.Applied)
                {
                    var effect = f.ClaimEffect("answer/" + need); var receipt = await f.Effects.ExecuteAsync(effect, default);
                    wire = await agent.NextMessageAsync().WaitAsync(DeliveryWait); responseRead = true;
                    actual = wire.GetProperty("result");
                    Assert.True(f.Complete(effect, receipt).Applied);
                    await AskRow(live, "answered");
                    replay = f.Db.Writer.Write("test.shared-owner-replay", await service.PrepareOwnerAsync(window, default));
                }
            }
            finally { await CloseOwnerPreparationAsk(f, agent, need, responseRead); }
            Assert.True(pendingBefore); Assert.True(pendingAfter); Assert.NotNull(accepted); Assert.True(accepted.Applied, accepted.Error);
            Assert.Equal(accepted, replay); Assert.Equal(receiptsBefore, receiptsAfter);
            Assert.Equal(JsonValueKind.Number, wire.GetProperty("id").ValueKind); Assert.Equal(7, wire.GetProperty("id").GetInt32());
            Assert.Equal(2000, f.Number("SELECT answered_ms FROM engine_needs_me WHERE id=$id", need));
            if (kind == "permission") Assert.Equal("yes", actual.GetProperty("outcome").GetProperty("optionId").GetString());
            else Assert.True(JsonElement.DeepEquals(expected, actual));
            Assert.Equal(1, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE id=$id", r => r.Long(0), default, new SqlArg("$id", "answer/" + need)));
            using var stored = JsonDocument.Parse(f.Db.QueryFirst("SELECT payload_json FROM engine_outbox WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", "answer/" + need))!);
            Assert.True(JsonElement.DeepEquals(expected, stored.RootElement.GetProperty("answer")));
            Assert.Equal(need, stored.RootElement.GetProperty("binding").GetProperty("chatRowId").GetString());
            Assert.EndsWith(":7", stored.RootElement.GetProperty("binding").GetProperty("providerRequestId").GetString());
        }
    }

    [Fact]
    public async Task Shared_owner_preparer_invalid_selection_records_refusal_without_completing_original_request()
    {
        await using var f = new AdapterDeliveryFixture();
        var (agent, live, need) = await OpenOwnerPreparationAsk(f, "permission"); using (live)
        {
            CommandResult? refused = null; bool stillPending = false;
            try
            {
                var prepare = await OwnerNeedService(f).PrepareOwnerAsync(OwnerWindow(f, need, JsonSerializer.SerializeToElement("missing-option")), default);
                refused = f.Db.Writer.Write("test.invalid-owner-selection", prepare);
                stillPending = f.Hosts.For(f.Session.SessionId).PrepareProviderAnswer(need, JsonSerializer.SerializeToElement("yes")) is not null;
                Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'", r => r.Long(0), default));
            }
            finally { await CloseOwnerPreparationAsk(f, agent, need, false); }
            Assert.NotNull(refused); Assert.False(refused.Applied); Assert.Equal("answer_preparation_required", refused.Error); Assert.True(stillPending);
        }
    }

    [Fact]
    public async Task Shared_owner_preparer_cancellation_after_capture_before_writer_creates_no_answer_or_receipt()
    {
        await using var f = new AdapterDeliveryFixture();
        var (agent, live, need) = await OpenOwnerPreparationAsk(f, "permission"); using (live)
        {
            Exception? error = null; long before = 0, after = 0;
            try
            {
                using var stop = new CancellationTokenSource();
                var prepare = await OwnerNeedService(f).PrepareOwnerAsync(OwnerWindow(f, need, JsonSerializer.SerializeToElement("yes")), stop.Token);
                before = f.Db.QueryFirst("SELECT COUNT(*) FROM engine_command_receipt", r => r.Long(0), default);
                stop.Cancel(); error = Record.Exception(() => f.Db.Writer.Write("test.cancelled-owner-answer", prepare));
                after = f.Db.QueryFirst("SELECT COUNT(*) FROM engine_command_receipt", r => r.Long(0), default);
                Assert.Equal("open", f.State("engine_needs_me", need));
                Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'", r => r.Long(0), default));
            }
            finally { await CloseOwnerPreparationAsk(f, agent, need, false); }
            Assert.IsAssignableFrom<OperationCanceledException>(error); Assert.Equal(before, after);
        }
    }

    [Fact]
    public async Task Shared_owner_preparer_current_epoch_is_rechecked_in_original_supplied_writer()
    {
        await using var f = new AdapterDeliveryFixture();
        var (agent, live, need) = await OpenOwnerPreparationAsk(f, "permission"); using (live)
        {
            CommandResult? refused = null;
            try
            {
                var prepare = await OwnerNeedService(f).PrepareOwnerAsync(OwnerWindow(f, need, JsonSerializer.SerializeToElement("yes")), default);
                f.Db.Writer.Write("test.owner-epoch-race", s => s.Execute("UPDATE engine_session SET epoch=epoch+1 WHERE id=$id", new SqlArg("$id", f.Session.SessionId)));
                refused = f.Db.Writer.Write("test.stale-owner-answer", prepare);
                Assert.Equal("open", f.State("engine_needs_me", need)); Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'", r => r.Long(0), default));
            }
            finally { await CloseOwnerPreparationAsk(f, agent, need, false, expectedStaleObservation: true); }
            Assert.NotNull(refused); Assert.Equal("stale_epoch", refused.Error);
        }
    }

    [Fact]
    public async Task Shared_owner_preparer_replays_old_oversized_refusal_after_real_provider_request_is_gone()
    {
        await using var f = new AdapterDeliveryFixture();
        var (agent, live, need) = await OpenOwnerPreparationAsk(f, "question"); using (live)
        {
            var original = JsonSerializer.SerializeToElement(new { action = "accept", content = new { body = new string('x', 70000) } });
            var window = OwnerWindow(f, need, original);
            var context = new CommandContext(window.CommandId, f.Runtime.Machine, new(ActorKind.Window, "owner"), need, 1, window.Epoch, 1000);
            var old = f.Db.Writer.Write("test.old-owner-size-refusal", s => f.Commands.Apply(s, context, window.Command));
            await CloseOwnerPreparationAsk(f, agent, need, false);
            var prepare = await OwnerNeedService(f).PrepareOwnerAsync(window, default);
            var replay = f.Db.Writer.Write("test.old-owner-size-replay", prepare);
            Assert.Equal("command_too_large", old.Error); Assert.Equal(old, replay);
            Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'", r => r.Long(0), default));
        }
    }

    private static NeedAnswers OwnerNeedService(AdapterDeliveryFixture f) => new(new(f.Runtime, new(() => 5, () => 10), f.Time), f.Runtime, f.Hosts, f.Time);
    private static WindowCommand OwnerWindow(AdapterDeliveryFixture f, string need, JsonElement answer) =>
        new(Guid.NewGuid(), need, 1, f.Number("SELECT epoch FROM engine_session WHERE id=$id", f.Session.SessionId), new ResolveNeed("answered", answer));

    private static async Task<(FakeLink Agent, LiveSubscription Live, string Need)> OpenOwnerPreparationAsk(AdapterDeliveryFixture f, string kind)
    {
        var send = f.Claim(f.Queue()); var sending = f.Effects.ExecuteAsync(send, default);
        var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt"); await agent.InsertAsync(prompt, "provider");
        var receipt = await sending.WaitAsync(DeliveryWait);
        Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition); Assert.True(f.Complete(send, receipt).Applied);
        var live = f.Hub.Subscribe(f.Session.SessionId, f.Rows.LastSeq(f.Session.SessionId));
        var form = JsonSerializer.Serialize(new { sessionId = FakeLink.AcpSessionId, mode = "form", message = "Full form", requestedSchema = new { type = "object" } });
        await agent.RequestAsync(7, kind == "permission" ? "session/request_permission" : "elicitation/create", kind == "permission" ? PermissionRequest.ReplaceLineEndings("") : form);
        return (agent, live, (await AskRow(live, "open")).ItemId![4..]);
    }

    private static async Task CloseOwnerPreparationAsk(AdapterDeliveryFixture f, FakeLink agent, string need, bool responseRead, bool expectedStaleObservation = false)
    {
        if (!responseRead)
        {
            var pending = f.Hosts.For(f.Session.SessionId).PrepareProviderAnswer(need, JsonSerializer.SerializeToElement(new { action = "decline" }))
                ?? f.Hosts.For(f.Session.SessionId).PrepareProviderAnswer(need, JsonSerializer.SerializeToElement("yes"));
            if (pending is not null)
            {
                var binding = pending.Binding; var observed = binding.Observation;
                f.Hosts.For(f.Session.SessionId).CancelProviderRequest(binding.ProviderRequestId,
                    new(binding.SessionId, binding.ChatRowId, observed.Epoch, observed.RunId, observed.StepId, observed.Attempt, "cancelled"));
            }
            var response = await agent.NextMessageAsync().WaitAsync(DeliveryWait);
            Assert.False(response.TryGetProperty("method", out _));
            Assert.Equal(JsonValueKind.Number, response.GetProperty("id").ValueKind);
            Assert.Equal(7, response.GetProperty("id").GetInt32());
            if (expectedStaleObservation)
            {
                var error = response.GetProperty("error");
                Assert.Equal(-32603, error.GetProperty("code").GetInt32());
                Assert.Equal("provider_request_changed", error.GetProperty("message").GetString());
            }
            else Assert.Equal(JsonValueKind.Object, response.GetProperty("result").ValueKind);
        }
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
    }
}
