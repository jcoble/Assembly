using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Effects;
using Assembly.Engine.NeedsMe;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;

namespace Assembly.Tests.Engine.Delivery;

public sealed partial class AdapterEffectsTests
{
    [Fact]
    public async Task Legacy_bindingless_effect_never_completes_a_live_request_but_exact_settled_evidence_can_reconcile()
    {
        await using var f = new AdapterDeliveryFixture();
        var send = f.Claim(f.Queue()); var sending = f.Effects.ExecuteAsync(send, default);
        var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "provider"); Assert.True(f.Complete(send, await sending.WaitAsync(DeliveryWait)).Applied);
        using var live = f.Hub.Subscribe(f.Session.SessionId, f.Rows.LastSeq(f.Session.SessionId));
        await agent.RequestAsync(7, "session/request_permission", PermissionRequest.ReplaceLineEndings(""));
        var asked = await AskRow(live, "open"); var id = asked.ItemId![4..];
        var host = f.Hosts.For(f.Session.SessionId);
        var answer = JsonSerializer.SerializeToElement(new { optionId = "yes" });
        var prepared = host.PrepareProviderAnswer(id, answer)!;
        // A persisted old payload has no binding. Seeding it is not a fresh owner-admission producer.
        var payload = JsonSerializer.Serialize(new NeedAnswer(id, prepared.Binding.ProviderRequestId, answer), global::Assembly.Engine.EngineJson.Default.NeedAnswer);
        f.Db.Writer.Write("test.legacy-answer", s =>
        {
            s.Execute("UPDATE engine_needs_me SET state='answered',version=2,answer_json=$answer WHERE id=$id", new("$answer", answer.GetRawText()), new("$id", id));
            return s.Execute("""
                INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,kind,payload_json,state,attempt)
                VALUES($id,0,$machine,1000,1000,0,$session,'answer',$payload,'pending',0)
                """, new SqlArg("$id", "answer/" + id), new("$machine", f.Runtime.Machine), new("$session", f.Session.SessionId), new("$payload", payload));
        });
        var effect = f.ClaimEffect("answer/" + id);
        var attempted = await f.Effects.ExecuteAsync(effect, default);
        var cleanupAccepted = host.AnswerProviderRequest(prepared.Binding, prepared.Answer);
        var actual = await agent.ResultAsync(); await AskRow(live, "answered");
        var reconciled = await f.Effects.ReconcileAsync(effect, default);
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
        Assert.Equal(EffectDisposition.Unknown, attempted.Disposition);
        Assert.True(cleanupAccepted); Assert.Equal("yes", actual.GetProperty("outcome").GetProperty("optionId").GetString());
        Assert.Equal(EffectDisposition.Confirmed, reconciled.Disposition);
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer' AND id=$id", effect.Id));
    }
    [Theory]
    [InlineData("epoch")]
    [InlineData("binding")]
    [InlineData("request")]
    [InlineData("valid")]
    public async Task Answer_consumer_requires_actual_owning_epoch_and_the_complete_original_binding(string change)
    {
        await using var f = new AdapterDeliveryFixture();
        var send = f.Claim(f.Queue()); var sending = f.Effects.ExecuteAsync(send, default);
        var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "provider"); Assert.True(f.Complete(send, await sending.WaitAsync(DeliveryWait)).Applied);
        using var live = f.Hub.Subscribe(f.Session.SessionId, f.Rows.LastSeq(f.Session.SessionId));
        await agent.RequestAsync(7, "session/request_permission", PermissionRequest.ReplaceLineEndings(""));
        var asked = await AskRow(live, "open"); var id = asked.ItemId![4..];
        var host = f.Hosts.For(f.Session.SessionId);
        var answer = JsonSerializer.SerializeToElement(new { optionId = "yes" });
        var prepared = host.PrepareProviderAnswer(id, answer)!;
        var binding = change switch
        {
            "binding" => prepared.Binding with { AcpSessionId = "another-open" },
            "request" => prepared.Binding with { ProviderRequestId = "another-request" },
            _ => prepared.Binding
        };
        var payload = JsonSerializer.SerializeToElement(new { needId = id, providerRequestId = prepared.Binding.ProviderRequestId, answer, binding },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var effect = SeedRetainedAnswer(f, id, payload, "answered", answer);
        if (change == "epoch") f.Db.Writer.Write("test.changed-epoch", s => s.Execute("UPDATE engine_session SET epoch=epoch+1 WHERE id=$id", new SqlArg("$id", f.Session.SessionId)));
        var attempted = await f.Effects.ExecuteAsync(effect, default);
        var cleanupAccepted = host.AnswerProviderRequest(prepared.Binding, prepared.Answer);
        var actual = await agent.ResultAsync(); await AskRow(live, "answered");
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
        Assert.Equal("yes", actual.GetProperty("outcome").GetProperty("optionId").GetString());
        if (change == "valid") { Assert.Equal(EffectDisposition.Confirmed, attempted.Disposition); Assert.False(cleanupAccepted); }
        else { Assert.NotEqual(EffectDisposition.Confirmed, attempted.Disposition); Assert.True(cleanupAccepted); }
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("question")]
    public async Task Cancellation_consumer_hands_off_exact_terminal_disposition_without_selecting_a_permission_option(string kind)
    {
        await using var f = new AdapterDeliveryFixture();
        var send = f.Claim(f.Queue()); var sending = f.Effects.ExecuteAsync(send, default);
        var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "provider"); Assert.True(f.Complete(send, await sending.WaitAsync(DeliveryWait)).Applied);
        using var live = f.Hub.Subscribe(f.Session.SessionId, f.Rows.LastSeq(f.Session.SessionId));
        await agent.RequestAsync(7, kind == "permission" ? "session/request_permission" : "elicitation/create",
            kind == "permission" ? PermissionRequest.ReplaceLineEndings("") : JsonSerializer.Serialize(new
            { sessionId = "acp-1", mode = "form", message = "Form", requestedSchema = new { type = "object" } }));
        var asked = await AskRow(live, "open"); var id = asked.ItemId![4..];
        var host = f.Hosts.For(f.Session.SessionId);
        var prepared = host.PrepareProviderAnswer(id, kind == "permission" ? JsonSerializer.SerializeToElement("yes") : JsonSerializer.SerializeToElement(new { action = "decline" }))!;
        var o = prepared.Binding.Observation;
        var cancellation = new ProviderTerminalBinding(f.Session.SessionId, id, o.Epoch, o.RunId, o.StepId, o.Attempt, "dismissed");
        var payload = JsonSerializer.SerializeToElement(new { needId = id, providerRequestId = prepared.Binding.ProviderRequestId, answer = (string?)null, cancellation },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var effect = SeedRetainedAnswer(f, id, payload, "dismissed", null);
        var attempted = await f.Effects.ExecuteAsync(effect, default);
        var cleanupAccepted = host.CancelProviderRequest(prepared.Binding.ProviderRequestId, cancellation);
        var actual = await agent.ResultAsync(); await AskRow(live, "cancelled");
        var reconciled = await f.Effects.ReconcileAsync(effect, default);
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
        Assert.Equal(EffectDisposition.Confirmed, attempted.Disposition); Assert.False(cleanupAccepted);
        Assert.Equal(EffectDisposition.Confirmed, reconciled.Disposition);
        if (kind == "permission") Assert.Equal("cancelled", actual.GetProperty("outcome").GetProperty("outcome").GetString());
        else Assert.Equal("cancel", actual.GetProperty("action").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public async Task Missing_legacy_chat_identity_cannot_match_a_decoy_row_and_retains_uncertainty_order_and_restart(string? chat)
    {
        await using var f = new AdapterDeliveryFixture();
        var send = f.Claim(f.Queue()); var sending = f.Effects.ExecuteAsync(send, default);
        var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "provider"); Assert.True(f.Complete(send, await sending.WaitAsync(DeliveryWait)).Applied);
        using var live = f.Hub.Subscribe(f.Session.SessionId, f.Rows.LastSeq(f.Session.SessionId));
        await agent.RequestAsync(7, "session/request_permission", PermissionRequest.ReplaceLineEndings(""));
        var asked = await AskRow(live, "open");
        var host = f.Hosts.For(f.Session.SessionId);
        var prepared = host.PrepareProviderAnswer(asked.ItemId![4..], JsonSerializer.SerializeToElement("yes"))!;
        var jsonNull = JsonSerializer.SerializeToElement((string?)null);
        var payload = JsonSerializer.Serialize(new NeedAnswer("legacy", "legacy-rpc", jsonNull), global::Assembly.Engine.EngineJson.Default.NeedAnswer);
        var decoy = new AskPayload("permission", "answered", "Decoy", null, null, [], null, jsonNull);
        f.Rows.Commit(f.Session.SessionId, new RowConverter().Apply(new AskChanged(1000, chat ?? "", decoy)), s =>
        {
            s.Execute("""
                INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,kind,text,options_json,
                    provider_request_id,chat_row_id,state,answer_json)
                VALUES('legacy',2,$machine,1000,1000,$session,'permission','Old','[]','legacy-rpc',$chat,'expired',NULL)
                """, new SqlArg("$machine", f.Runtime.Machine), new("$session", f.Session.SessionId), new("$chat", chat));
            s.Execute("""
                INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,kind,payload_json,state,attempt)
                VALUES('answer/legacy',0,$machine,1000,1000,0,$session,'answer',$payload,'pending',0)
                """, new SqlArg("$machine", f.Runtime.Machine), new("$session", f.Session.SessionId), new("$payload", payload));
        });
        var effect = f.ClaimEffect("answer/legacy"); var attempted = await f.Effects.ExecuteAsync(effect, default);
        Assert.True(f.Complete(effect, attempted).Applied);
        var cleanup = host.AnswerProviderRequest(prepared.Binding, prepared.Answer); var actual = await agent.ResultAsync();
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
        Assert.True(cleanup); Assert.Equal("yes", actual.GetProperty("outcome").GetProperty("optionId").GetString());
        Assert.Equal(EffectDisposition.Unknown, attempted.Disposition);
        Assert.Equal("uncertain", f.State("engine_outbox", "answer/legacy"));
        Assert.Equal(2000, f.Number("SELECT next_ms FROM engine_outbox WHERE id=$id", "answer/legacy"));
        f.Db.Writer.Write("test.later-effect", s => s.Execute("""
            INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,kind,payload_json,state,attempt)
            VALUES('later-stop',0,$machine,1000,1000,1,$session,'stop','{}','pending',0)
            """, new SqlArg("$machine", f.Runtime.Machine), new("$session", f.Session.SessionId)));
        Assert.Equal("unavailable", f.Apply(new ClaimEffect(), "later-stop", 0).Error);
        await f.RestartAsync();
        Assert.Equal("uncertain", f.State("engine_outbox", "answer/legacy")); Assert.Equal("pending", f.State("engine_outbox", "later-stop"));
        Assert.Equal(1, f.Number("SELECT attempt FROM engine_outbox WHERE id=$id", "answer/legacy"));
    }

    private static Effect SeedRetainedAnswer(AdapterDeliveryFixture f, string id, JsonElement payload, string state, JsonElement? answer)
    {
        f.Db.Writer.Write("test.retained-answer", s =>
        {
            s.Execute("UPDATE engine_needs_me SET state=$state,version=2,answer_json=$answer WHERE id=$id",
                new("$state", state), new("$answer", answer?.GetRawText()), new("$id", id));
            return s.Execute("""
                INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,kind,payload_json,state,attempt)
                VALUES($id,0,$machine,1000,1000,0,$session,'answer',$payload,'pending',0)
                """, new SqlArg("$id", "answer/" + id), new("$machine", f.Runtime.Machine), new("$session", f.Session.SessionId), new("$payload", payload.GetRawText()));
        });
        return f.ClaimEffect("answer/" + id);
    }

}
