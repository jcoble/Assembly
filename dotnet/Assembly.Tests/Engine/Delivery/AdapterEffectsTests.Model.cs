using Assembly.Tests.Engine;
using Assembly.Application.Common.Database;
using Assembly.Engine.Effects;

namespace Assembly.Tests.Engine.Delivery;

public sealed partial class AdapterEffectsTests
{
    [Fact]
    public async Task Unavailable_model_fails_before_prompt_preserves_original_and_requires_new_client_after_override()
    {
        await using var f = new AdapterDeliveryFixture();
        f.Db.Writer.Write("test.pending", s => s.Execute("INSERT INTO session_config(session_id,pending_model) VALUES($id,'unavailable')", new SqlArg("$id", f.Session.SessionId)));
        var text = "exact failed\0draft"; string[] refs = ["one.png", "two.png"];
        var id = f.Queue(text, "old", refs: refs); var first = f.Claim(id); var sending = f.Effects.ExecuteAsync(first, default);
        var agent = await f.Launcher.NextAsync(); await agent.StartAsync(configOptions: ModelOptions); await agent.CloseAsync();
        var refusal = await sending.WaitAsync(DeliveryWait); Assert.Equal("failed", Message(refusal).State); Assert.Equal("model_unavailable", Message(refusal).Reason);
        Assert.True(f.Complete(first, refusal).Applied); Assert.Equal("failed", f.State("engine_message", id)); Assert.Equal("unavailable", f.Sessions.Config(f.Session.SessionId).PendingModel);
        f.Sessions.ConfirmModelSelection(f.Session.SessionId, "model", "offered", ModelOptions); Assert.Null(f.Sessions.Config(f.Session.SessionId).PendingModel);
        var effects = f.Db.QueryFirst("SELECT COUNT(*) FROM engine_outbox", r => r.Long(0), default);
        Assert.Equal(id, f.Queue(text, "old", refs: refs)); Assert.Equal(effects, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_outbox", r => r.Long(0), default));
        var retry = f.Claim(f.Queue(text, "new", refs: refs)); var executing = f.Effects.ExecuteAsync(retry, default);
        await agent.OpenAsync(configOptions: ModelOptions); var prompt = await agent.ReadAsync("session/prompt"); Assert.Equal(text, prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        await agent.InsertAsync(prompt, "only-successful-prompt"); Assert.True(f.Complete(retry, await executing.WaitAsync(DeliveryWait)).Applied);
        Assert.Equal(refs, first.Payload.GetProperty("attachmentIds").EnumerateArray().Select(value => value.GetString())); Assert.Equal("failed", f.State("engine_message", id));
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
    }
    [Fact]
    public async Task Due_typed_model_unavailable_expires_without_provider_insertion_or_ack()
    {
        await using var f = new AdapterDeliveryFixture();
        f.Db.Writer.Write("test.pending", s => s.Execute("INSERT INTO session_config(session_id,pending_model) VALUES($id,'unavailable')", new SqlArg("$id", f.Session.SessionId)));
        var message = f.Queue("original draft", "due-model", refs: ["one.png"]); var effect = f.Claim(message); f.Time.Advance(TimeSpan.FromMinutes(30));
        var receipt = await f.Effects.ExecuteAsync(effect, default);
        Assert.Equal("expired", Message(receipt).Reason); Assert.True(f.Complete(effect, receipt).Applied);
        Assert.Equal(0, f.Launcher.Launches);
        Assert.Equal("failed", f.State("engine_message", message)); Assert.Equal("expired", f.Db.QueryFirst("SELECT reason FROM engine_message WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", message)));
        Assert.Equal("unavailable", f.Sessions.Config(f.Session.SessionId).PendingModel);
        using var scope = f.Db.OpenSnapshot(default); Assert.Null(f.Rows.UserDeliveryOfClient(scope, f.Session.SessionId, "due-model"));
    }

    private const string ModelOptions = """[{"id":"model","category":"model","type":"select","name":"Model","currentValue":"offered","options":[{"value":"offered","name":"Friendly"}]}]""";
}
