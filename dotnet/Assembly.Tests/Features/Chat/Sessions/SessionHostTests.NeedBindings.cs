using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Topics;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed partial class SessionHostTests
{
    [Fact]
    public async Task Prepared_permission_retains_every_original_binding_field_and_only_exact_options_normalize()
    {
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time));
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work, feed: new EngineFeed(runtime, _log, _time));
        var host = registry.For(_session.SessionId); host.Send("permission");
        var agent = await _launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.RequestAsync(7, "session/request_permission", BindingPermission("original"));
        var card = (await RowsUntil(r => r.Kind == "ask"))[^1]; var id = card.ItemId![4..];
        var invalid = host.PrepareProviderAnswer(id, JsonSerializer.SerializeToElement("allow"));
        var blank = host.PrepareProviderAnswer(id, JsonSerializer.SerializeToElement(""));
        var prepared = host.PrepareProviderAnswer(id, JsonSerializer.SerializeToElement("persistent"))!;
        var binding = prepared.Binding;
        var variants = new[]
        {
            binding with { SessionId = "other" }, binding with { ChatRowId = "other" },
            binding with { ProviderRequestId = "other" }, binding with { AcpSessionId = "other" },
            binding with { Observation = binding.Observation with { SessionId = "other" } },
            binding with { Observation = binding.Observation with { Epoch = binding.Observation.Epoch + 1 } },
            binding with { Observation = binding.Observation with { RunId = "other" } },
            binding with { Observation = binding.Observation with { StepId = "other" } },
            binding with { Observation = binding.Observation with { Attempt = 999 } }
        };
        var refused = variants.Select(changed => host.AnswerProviderRequest(changed, prepared.Answer)).ToArray();
        var response = agent.NextMessageAsync(); var pending = !response.IsCompleted;
        var accepted = host.AnswerProviderRequest(binding, prepared.Answer);
        var repeated = host.AnswerProviderRequest(binding, prepared.Answer);
        var actual = await response.WaitAsync(Wait);
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}"""); await agent.CloseAsync();
        Assert.Null(invalid); Assert.NotNull(blank); Assert.Equal("", blank.Answer.GetProperty("optionId").GetString());
        Assert.All(refused, value => Assert.False(value)); Assert.True(pending);
        Assert.True(accepted); Assert.False(repeated);
        Assert.Equal("persistent", prepared.Answer.GetProperty("optionId").GetString());
        Assert.Equal("persistent", actual.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
        Assert.Equal(_session.SessionId, binding.SessionId); Assert.Equal(id, binding.ChatRowId);
        Assert.EndsWith(":7", binding.ProviderRequestId); Assert.Equal(FakeLink.AcpSessionId, binding.AcpSessionId);
        Assert.Null(host.PrepareProviderAnswer(id, prepared.Answer));
    }

    [Fact]
    public async Task Numeric_and_string_RPC_ids_and_reused_numeric_id_remain_bound_to_their_original_chat_asks()
    {
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time));
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work, feed: new EngineFeed(runtime, _log, _time));
        var host = registry.For(_session.SessionId); host.Send("concurrent asks");
        var agent = await _launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.RequestAsync(7, "session/request_permission", BindingPermission("numeric"));
        var numeric = (await RowsUntil(r => r.Kind == "ask" && r.Payload.GetProperty("title").GetString() == "numeric"))[^1];
        await agent.RequestAsync("7", "session/request_permission", BindingPermission("string"));
        var quoted = (await RowsUntil(r => r.Kind == "ask" && r.Payload.GetProperty("title").GetString() == "string"))[^1];
        var first = host.PrepareProviderAnswer(numeric.ItemId![4..], JsonSerializer.SerializeToElement("once"))!;
        var second = host.PrepareProviderAnswer(quoted.ItemId![4..], JsonSerializer.SerializeToElement("persistent"))!;
        Assert.True(host.AnswerProviderRequest(second.Binding, second.Answer)); var secondResult = await agent.NextMessageAsync();
        Assert.True(host.AnswerProviderRequest(first.Binding, first.Answer)); var firstResult = await agent.NextMessageAsync();
        await agent.RequestAsync(7, "session/request_permission", BindingPermission("replacement"));
        var replacement = (await RowsUntil(r => r.Kind == "ask" && r.Payload.GetProperty("title").GetString() == "replacement"))[^1];
        var next = host.PrepareProviderAnswer(replacement.ItemId![4..], JsonSerializer.SerializeToElement("once"))!;
        var staleAccepted = host.AnswerProviderRequest(first.Binding, first.Answer);
        Assert.True(host.AnswerProviderRequest(next.Binding, next.Answer)); var nextResult = await agent.NextMessageAsync();
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}"""); await agent.CloseAsync();
        Assert.Equal(JsonValueKind.String, secondResult.GetProperty("id").ValueKind); Assert.Equal("7", secondResult.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Number, firstResult.GetProperty("id").ValueKind); Assert.Equal(7, nextResult.GetProperty("id").GetInt32());
        Assert.NotEqual(first.Binding.ProviderRequestId, second.Binding.ProviderRequestId);
        Assert.NotEqual(first.Binding.ProviderRequestId, next.Binding.ProviderRequestId);
        Assert.NotEqual(first.Binding.ChatRowId, next.Binding.ChatRowId); Assert.False(staleAccepted);
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("question")]
    public async Task Bound_terminal_cancellation_ends_only_the_original_request_with_actual_cancel_response(string kind)
    {
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time));
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work, feed: new EngineFeed(runtime, _log, _time));
        var host = registry.For(_session.SessionId); host.Send("cancel one ask");
        var agent = await _launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.RequestAsync(7, kind == "permission" ? "session/request_permission" : "elicitation/create",
            kind == "permission" ? BindingPermission("cancel me") : JsonSerializer.Serialize(new
            { sessionId = FakeLink.AcpSessionId, mode = "form", message = "Form", requestedSchema = new { type = "object" } }));
        var card = (await RowsUntil(r => r.Kind == "ask"))[^1]; var id = card.ItemId![4..];
        var prepared = host.PrepareProviderAnswer(id, kind == "permission" ? JsonSerializer.SerializeToElement("once") : JsonSerializer.SerializeToElement(new { action = "decline" }))!;
        var o = prepared.Binding.Observation;
        var terminal = new ProviderTerminalBinding(_session.SessionId, id, o.Epoch, o.RunId, o.StepId, o.Attempt, "dismissed");
        var variants = new[] { terminal with { Epoch = o.Epoch + 1 }, terminal with { SessionId = "other" },
            terminal with { ChatRowId = "other" }, terminal with { RunId = "other" },
            terminal with { StepId = "other" }, terminal with { Attempt = 999 } };
        var refused = variants.Select(changed => host.CancelProviderRequest(prepared.Binding.ProviderRequestId, changed)).ToArray();
        var wrongRequest = host.CancelProviderRequest("different-request", terminal);
        var cancelled = host.CancelProviderRequest(prepared.Binding.ProviderRequestId, terminal);
        var repeated = host.CancelProviderRequest(prepared.Binding.ProviderRequestId, terminal);
        var actual = await agent.ResultAsync();
        await RowsUntil(r => r.Kind == "ask" && r.Payload.GetProperty("state").GetString() == "cancelled");
        var stillRunning = host.ActiveRunId;
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}"""); await agent.CloseAsync();
        Assert.All(refused, value => Assert.False(value)); Assert.False(wrongRequest);
        Assert.True(cancelled); Assert.False(repeated); Assert.NotNull(stillRunning);
        if (kind == "permission") Assert.Equal("cancelled", actual.GetProperty("outcome").GetProperty("outcome").GetString());
        else Assert.Equal("cancel", actual.GetProperty("action").GetString());
        Assert.Null(host.PrepareProviderAnswer(id, prepared.Answer));
    }

    [Fact]
    public void Original_request_occurrence_is_stable_distinguishes_RPC_types_and_preserves_legacy_identity_vectors()
    {
        var occurrence = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var numeric = new JsonRpcRequestContext("connection", JsonSerializer.SerializeToElement(7), occurrence);
        var quoted = numeric with { Id = JsonSerializer.SerializeToElement("7") };
        Assert.Equal("connection/00112233445566778899aabbccddeeff:7", numeric.ProviderRequestId);
        Assert.Equal(numeric.ProviderRequestId, numeric.ProviderRequestId);
        Assert.Equal("connection/00112233445566778899aabbccddeeff:\"7\"", quoted.ProviderRequestId);
        Assert.NotEqual(numeric.ProviderRequestId, (numeric with { OccurrenceId = Guid.Parse("10112233-4455-6677-8899-aabbccddeeff") }).ProviderRequestId);
        Assert.Equal("connection:7", (numeric with { OccurrenceId = null }).ProviderRequestId);
        Assert.Equal("connection:\"7\"", (quoted with { OccurrenceId = null }).ProviderRequestId);
        Assert.Equal("connection", numeric.ConnectionId); Assert.Equal(7, numeric.Id.GetInt32());
    }

    private static string BindingPermission(string title) => JsonSerializer.Serialize(new
    {
        sessionId = FakeLink.AcpSessionId,
        title,
        options = new[] { new { optionId = "once", name = "Once", kind = "allow_once" }, new { optionId = "persistent", name = "Persistent", kind = "allow_always" }, new { optionId = "", name = "Blank exact ID", kind = "reject_once" } }
    });
}
