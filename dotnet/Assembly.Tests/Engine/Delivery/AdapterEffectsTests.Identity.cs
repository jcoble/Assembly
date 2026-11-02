using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.Delivery;

public sealed partial class AdapterEffectsTests
{
    [Fact]
    public async Task Session_prompt_and_injected_steer_keep_actual_identity_captured_label_and_positive_ack_on_reopen()
    {
        await using var f = new AdapterDeliveryFixture(); var title = "Captured [\"😀\"] " + new string('x', 2000);
        var source = f.Sessions.Helper(f.Session, "claude", title);
        var label = MessageText.Label(title, source.SessionId, "session") + "\0tail";
        void Queue(string id)
        {
            var result = f.Apply(new SendMessage(source.SessionId, f.Session.SessionId, "body 😀", "prompt"), id, 0, new(ActorKind.Session, source.SessionId)); Assert.True(result.Applied, result.Error);
            // Isolate the receiving boundary from A31's separately owned full-title producer.
            // Freeze only the captured label/body pair on this actual Engine-created message/effect tuple.
            f.Db.Writer.Write("test.frozen-full-label", scope =>
            {
                var payload = scope.QueryFirst("SELECT payload_json FROM engine_outbox WHERE json_extract(payload_json,'$.messageId')=$id", r => r.Text(0), new SqlArg("$id", id))!;
                var intent = JsonSerializer.Deserialize(payload, global::Assembly.Engine.EngineJson.Default.DeliverMessage)!;
                var frozen = intent with { SenderLabel = label, Text = MessageText.Prompt(label, "body 😀") };
                scope.Execute("UPDATE engine_message SET sender_label=CAST($label AS TEXT) WHERE id=$id", new SqlArg("$label", System.Text.Encoding.UTF8.GetBytes(label)), new("$id", id));
                scope.Execute("UPDATE engine_outbox SET payload_json=$payload WHERE json_extract(payload_json,'$.messageId')=$id", new SqlArg("$payload", JsonSerializer.Serialize(frozen, global::Assembly.Engine.EngineJson.Default.DeliverMessage)), new("$id", id)); return 0;
            });
        }
        Queue("source-prompt"); var first = f.Claim("source-prompt");
        Assert.Equal(label, f.Db.QueryFirst("SELECT CAST(sender_label AS BLOB) FROM engine_message WHERE id='source-prompt'", r => new System.Text.UTF8Encoding(false, true).GetString(r.Blob(0)), default));
        var actualIntent = first.Payload.Deserialize(global::Assembly.Engine.EngineJson.Default.DeliverMessage)!;
        Assert.Equal(label, actualIntent.SenderLabel); Assert.Equal(MessageText.Prompt(label, "body 😀"), actualIntent.Text);
        Assert.Equal(first.Payload.GetRawText(), f.Db.QueryFirst("SELECT payload_json FROM engine_outbox WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", first.Id)));
        var sending = f.Effects.ExecuteAsync(first, default); Assert.False(sending.IsCompleted, sending.IsCompleted ? (await sending).ToString() : null); var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        Assert.Equal(MessageText.Prompt(label, "body 😀"), prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        await agent.InsertAsync(prompt, "real-source-prompt"); Assert.True(f.Complete(first, await sending.WaitAsync(DeliveryWait)).Applied);
        Queue("source-steer"); var second = f.Claim("source-steer"); Assert.Equal("steer", second.Payload.GetProperty("how").GetString());
        var steering = f.Effects.ExecuteAsync(second, default); var request = await agent.ReadAsync("_session/steering");
        Assert.Equal(MessageText.Prompt(label, "body 😀"), request.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        await agent.ReplyAsync(request, """{"outcome":"injected"}"""); Assert.True(f.Complete(second, await steering.WaitAsync(DeliveryWait)).Applied);
        foreach (var id in new[] { "source-prompt", "source-steer" })
        {
            using var scope = f.Db.OpenSnapshot(default); var row = f.Rows.UserDeliveryOfClient(scope, f.Session.SessionId, id)!;
            Assert.Equal(source.SessionId, row.From); Assert.Equal(label, row.SenderLabel); Assert.NotNull(row.Acknowledgment);
        }
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
        await f.RestartAsync();
        using var reopened = f.Db.OpenSnapshot(default); Assert.Equal(label, f.Rows.UserDeliveryOfClient(reopened, f.Session.SessionId, "source-steer")!.SenderLabel);
        Assert.Equal(1, f.Launcher.Launches); Assert.Equal(2, f.Rows.RowsAfter(f.Session.SessionId, 0).Count(row => row.Kind == "user"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Active_same_client_conflicts_on_either_source_or_label_independently(bool changeSource)
    {
        await using var f = new AdapterDeliveryFixture(); var host = f.Hosts.For(f.Session.SessionId);
        var original = host.SendAsync("body", "joined", "Captured", default, "peer/x");
        var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        var error = await Assert.ThrowsAsync<CallFailed>(() => host.SendAsync("body", "joined", changeSource ? "Captured" : "Changed", default, changeSource ? "other" : "peer/x"));
        Assert.Equal("clientId already used for another message", error.Message);
        await agent.InsertAsync(prompt, "original-only"); await original.WaitAsync(DeliveryWait);
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
    }

    [Theory]
    [InlineData("from")]
    [InlineData("senderLabel")]
    public async Task Completion_source_or_label_conflict_rolls_back_without_ack_or_receipt(string field)
    {
        await using var f = new AdapterDeliveryFixture(); var first = f.Claim(f.Queue(raw: false));
        var sending = f.Effects.ExecuteAsync(first, default); var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "accepted-original"); var receipt = await sending.WaitAsync(DeliveryWait);
        f.Db.Writer.Write("test.corrupt-pair", s => s.Execute($"UPDATE rows SET payload=json_set(payload,'$.{field}','wrong') WHERE session_id=$id AND kind='user'", new SqlArg("$id", f.Session.SessionId)));
        Assert.Throws<CallFailed>(() => f.Complete(first, receipt)); Assert.Equal("inflight", f.State("engine_outbox", first.Id));
        using (var scope = f.Db.OpenSnapshot(default)) Assert.Null(f.Rows.UserDeliveryOfClient(scope, f.Session.SessionId, "message-C")!.Acknowledgment);
        Assert.Equal(Assembly.Engine.Effects.EffectDisposition.Unknown, (await f.Effects.ReconcileAsync(first, default)).Disposition);
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
    }
    [Fact]
    public async Task System_child_end_provenance_does_not_become_clickable_sender_identity()
    {
        await using var f = new AdapterDeliveryFixture(); var child = f.Sessions.Helper(f.Session, "claude", "Child");
        var ended = f.Apply(new SetSessionState("ended", null, null), child.SessionId, f.Number("SELECT version FROM engine_session WHERE id=$id", child.SessionId)); Assert.True(ended.Applied, ended.Error);
        var id = "child-ended/" + child.SessionId;
        Assert.Equal(child.SessionId, f.Db.QueryFirst("SELECT from_session_id FROM engine_message WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", id)));
        var effect = f.Claim(id); var sending = f.Effects.ExecuteAsync(effect, default); var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "system-provider"); Assert.True(f.Complete(effect, await sending.WaitAsync(DeliveryWait)).Applied);
        using (var scope = f.Db.OpenSnapshot(default)) { var row = f.Rows.UserDeliveryOfClient(scope, f.Session.SessionId, id)!; Assert.Null(row.From); Assert.Equal("Assembly", row.SenderLabel); }
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
    }

    [Fact]
    public async Task Auth_own_row_retry_retains_immutable_source_and_label_after_source_title_changes()
    {
        await using var f = new AdapterDeliveryFixture(); var source = f.Sessions.Helper(f.Session, "claude", "Original title");
        var queued = f.Apply(new SendMessage(source.SessionId, f.Session.SessionId, "same body", "prompt"), "auth-source", 0, new(ActorKind.Session, source.SessionId)); Assert.True(queued.Applied, queued.Error);
        var first = f.Claim("auth-source"); var sending = f.Effects.ExecuteAsync(first, default); var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyErrorAsync(prompt, -32000, "sign in"); var refused = await sending.WaitAsync(DeliveryWait); Assert.True(f.Complete(first, refused).Applied); var reference = Message(refused).UserRow;
        await agent.CloseAsync(); await f.RestartAsync(); f.Sessions.SetTitle(source.SessionId, "Changed current title", "manual");
        Assert.True(f.Apply(new SetSessionState("lost", null, "test"), f.Session.SessionId, f.Number("SELECT version FROM engine_session WHERE id=$id", f.Session.SessionId)).Applied);
        Assert.True(f.Apply(new SetSessionState("idle", null, null), f.Session.SessionId, f.Number("SELECT version FROM engine_session WHERE id=$id", f.Session.SessionId)).Applied);
        var retry = f.Claim("auth-source"); var resending = f.Effects.ExecuteAsync(retry, default); var next = await f.Launcher.NextAsync(); await next.StartAsync(); var actual = await next.ReadAsync("session/prompt");
        await next.InsertAsync(actual, "one-successful-source"); var receipt = await resending.WaitAsync(DeliveryWait); Assert.Equal(reference, Message(receipt).UserRow); Assert.True(f.Complete(retry, receipt).Applied);
        using (var scope = f.Db.OpenSnapshot(default)) { var row = f.Rows.UserDeliveryOfClient(scope, f.Session.SessionId, "auth-source")!; Assert.Equal(source.SessionId, row.From); Assert.Equal(MessageText.Label("Original title", source.SessionId, "session"), row.SenderLabel); }
        Assert.Single(f.Rows.RowsAfter(f.Session.SessionId, 0), r => r.Kind == "user");
        await next.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await next.CloseAsync();
    }
    [Fact]
    public async Task Session_sender_without_an_actual_source_is_unknown_before_provider_work()
    {
        await using var f = new AdapterDeliveryFixture(); var id = f.Queue(raw: false); var effect = f.Claim(id);
        f.Db.Writer.Write("test.invalid-source", scope => scope.Execute("UPDATE engine_message SET sender_kind='session',from_session_id=NULL WHERE id=$id", new SqlArg("$id", id)));
        Assert.Equal(Assembly.Engine.Effects.EffectDisposition.Unknown, (await f.Effects.ExecuteAsync(effect, default)).Disposition);
        Assert.Equal(0, f.Launcher.Launches);
    }
}
