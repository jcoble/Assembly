using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Messages;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Http;

public sealed partial class EngineHostTests
{
    private const string HostPermissionRequest = """
        {"sessionId":"acp-1","title":"Allow?","subject":{"type":"tool_call","toolCall":{"toolCallId":"t1","title":"Allow?"}},
         "options":[{"optionId":"allow","name":"Allow","kind":"allow_once"},{"optionId":"deny","name":"Deny","kind":"reject_once"}]}
        """;

    private static void SubmitHostFact(Host running, string id, EngineCommand command, long version = 0, EngineActor? actor = null)
    {
        var result = running.Runtime.Submit(new(Guid.NewGuid(), running.Runtime.Machine,
            actor ?? new(ActorKind.System, "adapter"), id, version, 0, TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()), command);
        Assert.True(result.Applied, result.Error);
    }

    private static async Task<FakeLink> OpenHostTurn(Host running, ClientWebSocket socket, FakeLauncher launcher, string session, string message)
    {
        await Command(socket, message, new SendMessage(null, session, "Work", "prompt"));
        var agent = await launcher.NextAsync();
        await agent.StartAsync(configOptions: """[{"id":"model","category":"model","type":"select","currentValue":"model","options":[{"value":"model","name":"Model"}]}]""");
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, message + "-provider");
        await running.WaitState("messages", message, "next_turn");
        return agent;
    }

    private static async Task AcceptHostSteering(FakeLink agent, string? text)
    {
        var steering = await agent.ReadAsync("_session/steering");
        if (text is not null)
            Assert.Contains(text, steering.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        await agent.ReplyAsync(steering, """{"outcome":"injected"}""");
    }

    private static async Task FinishHostTurn(FakeLink agent)
    {
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
        Assert.Equal(FakeLink.AcpSessionId, await agent.CloseAsync());
    }

    private static async Task WaitHostAnswerDone(Host running, string need)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (((IEngineRuntime)running.Runtime).Read(scope => scope.QueryFirst(
                "SELECT state FROM engine_outbox WHERE id=$id", row => row.Text(0), new SqlArg("$id", "answer/" + need)), default) == "done")
                done.TrySetResult();
        }
        using var subscribed = running.Runtime.Publisher.Register(_ => Check(), out _);
        Check();
        await done.Task.WaitAsync(Timeout);
    }

    private static void AssertHostAnswerExactlyOnce(Host running, string need)
    {
        var answers = ((IEngineRuntime)running.Runtime).Read(scope => scope.Query("""
            SELECT id,state,attempt,json_extract(payload_json,'$.providerRequestId'),
                json_extract(receipt_json,'$.data.providerRequestId') FROM engine_outbox WHERE kind='answer'
            """, row => (Id: row.Text(0), State: row.Text(1), Attempt: row.Long(2), Request: row.Text(3), Receipt: row.Text(4))), default);
        var answer = Assert.Single(answers);
        Assert.Equal("answer/" + need, answer.Id);
        Assert.Equal("done", answer.State);
        Assert.Equal(1, answer.Attempt);
        Assert.Equal(answer.Request, answer.Receipt);
        Assert.False(string.IsNullOrWhiteSpace(answer.Request));
    }
}
