using CallFailed = Assembly.Application.Common.Calls.CallFailed;
using Assembly.Infrastructure.Chat.Attachments;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Workflows;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Application.Common;
using Assembly.Tests.Features.Chat.Sessions;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Workflows.Contracts;
using Assembly.Infrastructure.Mcp;
using Assembly.Tests.Draft.Base.Chat;

namespace Assembly.Tests.Draft.Board;

public sealed class BoardToolsTests
{
    [Fact]
    public async Task RapidAnswersEachStartTheirOwnTurn()
    {
        await using var host = new BoardSocketTests.Host();
        var session = host.Sessions.Create("codex", "project");
        var args = Enumerable.Range(1, 3).Select(i => host.F.Answer("q" + i)).ToArray();
        foreach (var answer in args)
            host.Tools.Update(session.SessionId, host.F.Report(question: host.F.Ask(answer.QuestionKey)));
        await host.Tools.AnswerAsync(args[0], CancellationToken.None);
        var agent = await host.Launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        var later = args.Skip(1).Select(a => host.Tools.AnswerAsync(a, CancellationToken.None)).ToArray();
        for (var i = 0; i < 3; i++)
        {
            await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
            // Each turn closes before another opens; any steering request fails here.
            await agent.CloseAsync();
            if (i == 2) break;
            await agent.OpenAsync();
            prompt = await agent.ReadAsync("session/prompt");
        }
        await Task.WhenAll(later).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(host.F.Detail().Questions, q => Assert.Equal("delivered", q.DeliveryState));
        var rows = host.Rows.RowsAfter(session.SessionId, 0).Where(row => row.Kind == "user").ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal(3, rows.Select(row => row.RunId).Distinct().Count());
        foreach (var answer in args)
            Assert.Single(rows, row => row.Payload.GetProperty("clientId").GetString() == "board-answer/" + answer.AnswerKey);
    }

    [Fact]
    public void SameTaskKeyUpdatesOneCard()
    {
        using var f = new BoardQueryTests.Fixture();
        var first = f.Update();
        Assert.Equal(first, f.Update());
        Assert.Single(f.Page().Items);
        Assert.Single(f.Detail().Feed);
        Assert.Throws<CallFailed>(() => f.Transitions.Update("foreign", "project", f.Report()));
        Assert.Equal("session", f.Detail().Task.CreatorSessionId);
    }

    [Theory]
    [InlineData("text", "text", "Use it")]
    [InlineData("approval", "approve", null)]
    [InlineData("approval", "deny", null)]
    public async Task TwoQuestionsRemainIndependent(string kind, string answerKind, string? text)
    {
        await using var host = new BoardSocketTests.Host();
        var f = host.F;
        var sessions = host.Sessions;
        var session = sessions.Create("codex", "project");
        await using var tools = new BoardTools(sessions, host.Registry, f.Transitions, f.Queries, CancellationToken.None);
        tools.Update(session.SessionId, f.Report(question: f.Ask(kind: kind)));
        tools.Update(session.SessionId, f.Report(question: f.Ask("q2")));
        var args = f.Answer(value: new(answerKind, text));
        await tools.AnswerAsync(args, CancellationToken.None);
        Assert.Equal(1, Assert.Single(f.Page().Items).QuestionCount);
        var message = Assert.Single(host.Rows.RowsAfter(session.SessionId, 0), row => row.Kind == "user").Payload.GetProperty("text").GetString();
        var identity = $"""question "q1" (task "task", generation 1, answer key "{args.AnswerKey}")""";
        Assert.Equal(answerKind switch
        {
            "approve" => "Approved: " + identity,
            "deny" => "Denied: " + identity,
            _ => $"Answer to {identity}: {text}"
        }, message);
        Assert.Equal("delivered", f.Detail().Questions.Single(q => q.QuestionKey == "q1").DeliveryState);
    }

    [Fact]
    public void ReaskRejectsStaleGeneration()
    {
        using var f = new BoardQueryTests.Fixture();
        f.Update(question: f.Ask());
        var answer = f.Answer();
        var original = f.Transitions.Answer(answer, true).Acceptance;
        f.Update(question: f.Ask(generation: 1));
        Assert.Equal(2, Assert.Single(f.Detail().Questions).Generation);
        Assert.Equal(original, f.Transitions.Answer(answer, true).Acceptance);
        Assert.Contains("stale_generation", Assert.Throws<CallFailed>(() => f.Transitions.Answer(f.Answer(), true)).Message);
        Assert.Null(Assert.Single(f.Detail().Questions).Answer);
    }

    [Fact]
    public async Task DuplicateAnswerKeepsAcceptanceAndSendsOnce()
    {
        await using var host = new BoardSocketTests.Host();
        var f = host.F;
        var sessions = host.Sessions;
        var session = sessions.Create("claude", "project");
        await using var tools = new BoardTools(sessions, host.Registry, f.Transitions, f.Queries, CancellationToken.None);
        tools.Update(session.SessionId, f.Report(question: f.Ask()));
        var args = f.Answer();
        var accepted = await tools.AnswerAsync(args, CancellationToken.None);
        Assert.Equal(Assert.Single(f.Detail().Questions).DeliveryClientId,
            Assert.Single(host.Rows.RowsAfter(session.SessionId, 0), row => row.Kind == "user").Payload.GetProperty("clientId").GetString());
        Assert.Equal(accepted, await tools.AnswerAsync(args, CancellationToken.None));
        Assert.Single(host.Rows.RowsAfter(session.SessionId, 0), row => row.Kind == "user");
        await Assert.ThrowsAsync<CallFailed>(() => tools.AnswerAsync(f.Answer(), CancellationToken.None));
        await Assert.ThrowsAsync<CallFailed>(() => tools.AnswerAsync(args with { Answer = new("text", "Different") }, CancellationToken.None));
        Assert.Equal("board-answer/" + args.AnswerKey, Assert.Single(f.Detail().Questions).DeliveryClientId);
    }

    [Fact]
    public async Task HostStopCancelsHeldDeliveryAfterCallerLeaves()
    {
        await using var host = new BoardSocketTests.Host();
        var f = host.F;
        var sessions = host.Sessions;
        var session = sessions.Create("codex", "project");
        host.Registry.For(session.SessionId).Send("held");
        using var hostStop = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var tools = new BoardTools(sessions, host.Registry, f.Transitions, f.Queries, hostStop.Token);
        tools.Update(session.SessionId, f.Report(question: f.Ask()));
        var sending = tools.AnswerAsync(f.Answer(), caller.Token);
        Assert.False(sending.IsCompleted);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.NotNull(host.Registry.For(session.SessionId).ActiveRunId);
        Assert.Equal("pending", Assert.Single(f.Detail().Questions).DeliveryState);
        await hostStop.CancelAsync();
        await tools.DisposeAsync();
        Assert.Equal("delivery_unknown", Assert.Single(f.Detail().Questions).DeliveryState);
    }

    [Fact]
    public void RecoverySettlesMoreThanOneBatch()
    {
        using var f = new BoardQueryTests.Fixture();
        for (var i = 0; i < 205; i++)
        {
            var key = $"task-{i}";
            f.Update(key, question: f.Ask());
            f.Transitions.Answer(f.Answer() with { TaskKey = key });
        }
        f.Transitions.Recover();
        for (var i = 0; i < 205; i++)
            Assert.Equal("delivery_unknown", Assert.Single(f.Detail($"task-{i}").Questions).DeliveryState);
    }

    [Fact]
    public async Task ChatAnswerRecordsWithoutSend()
    {
        await using var host = new BoardSocketTests.Host();
        var f = host.F;
        var sessions = host.Sessions;
        var session = sessions.Create("claude", "project");
        await using var tools = new BoardTools(sessions, host.Registry, f.Transitions, f.Queries, CancellationToken.None);
        tools.Update(session.SessionId, f.Report(question: f.Ask()));
        var a = f.Answer();
        var received = new BoardAnswerReceived(a.QuestionKey, a.Generation, a.AnswerKey, a.Answer);
        var result = tools.Update(session.SessionId, f.Report(received: received));
        Assert.Equal(result, tools.Update(session.SessionId, f.Report(received: received)));
        Assert.Empty(host.Rows.RowsAfter(session.SessionId, 0));
        Assert.Equal("recorded_in_chat", Assert.Single(f.Detail().Questions).DeliveryState);
    }

    [Fact]
    public void RestartBetweenIntentAndSendIsUnknown() => RestartUnknown();

    [Fact]
    public async Task RestartBetweenSendAndAckIsUnknown()
    {
        await using var host = new BoardSocketTests.Host();
        var f = host.F;
        var sessions = host.Sessions;
        var session = sessions.Create("codex", "project");
        f.Transitions.Update(session.SessionId, "project", f.Report(question: f.Ask()));
        var args = f.Answer();
        var acceptance = f.Transitions.Answer(args).Acceptance;
        host.Registry.For(session.SessionId).Send("Board answer persisted before send", "board-answer/" + args.AnswerKey);
        f.Transitions.Recover();
        Assert.Equal("delivery_unknown", Assert.Single(f.Detail().Questions).DeliveryState);
        await using var tools = new BoardTools(sessions, host.Registry, f.Transitions, f.Queries, CancellationToken.None);
        Assert.Equal(acceptance, await tools.AnswerAsync(args, CancellationToken.None));
        Assert.Single(host.Rows.RowsAfter(session.SessionId, 0), row => row.Kind == "user");
    }

    private static void RestartUnknown()
    {
        using var f = new BoardQueryTests.Fixture();
        f.Update(question: f.Ask());
        var args = f.Answer();
        var accepted = f.Transitions.Answer(args).Acceptance;
        // The persisted boundary is identical if a send happened but no acknowledgment was committed.
        Assert.Equal("pending", Assert.Single(f.Detail().Questions).DeliveryState);
        f.Transitions.Recover();
        Assert.Equal("delivery_unknown", Assert.Single(f.Detail().Questions).DeliveryState);
        Assert.Equal("session", f.Detail().Task.CreatorSessionId);
        Assert.Equal(accepted, f.Transitions.Answer(args).Acceptance);
        Assert.False(f.Transitions.Answer(args).Created);

    }

    [Fact]
    public async Task CancelledWaitingDeliveryRetainsAnswer()
    {
        await using var host = new BoardSocketTests.Host();
        var f = host.F;
        var sessions = host.Sessions;
        var session = sessions.Create("claude", "project");
        host.Registry.For(session.SessionId).Send("held");
        using var hostStop = new CancellationTokenSource();
        await using var tools = new BoardTools(sessions, host.Registry, f.Transitions, f.Queries, hostStop.Token);
        tools.Update(session.SessionId, f.Report(question: f.Ask()));
        var args = f.Answer();
        var sending = tools.AnswerAsync(args, CancellationToken.None);
        await hostStop.CancelAsync();
        await sending;
        var q = Assert.Single(f.Detail().Questions);
        Assert.Equal(args.Answer, q.Answer);
        Assert.Equal("delivery_unknown", q.DeliveryState);
    }

    [Fact]
    public async Task ReaskWhileDeliveryPendingCannotReplaceQuestion()
    {
        await using var host = new BoardSocketTests.Host();
        var f = host.F;
        var sessions = host.Sessions;
        var session = sessions.Create("codex", "project");
        host.Registry.For(session.SessionId).Send("held");
        await using var tools = new BoardTools(sessions, host.Registry, f.Transitions, f.Queries, CancellationToken.None);
        tools.Update(session.SessionId, f.Report(question: f.Ask()));
        var args = f.Answer();
        var sending = tools.AnswerAsync(args, CancellationToken.None);
        Assert.False(sending.IsCompleted);
        Assert.Throws<CallFailed>(() => tools.Update(session.SessionId, f.Report(question: f.Ask(generation: 1))));
        var agent = await host.Launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        await sending;
        tools.Update(session.SessionId, f.Report(question: f.Ask(generation: 1)));
        var revision = f.Detail().Task.Revision;
        f.Transitions.Settle(args, "delivered", "late");
        Assert.Equal(revision, f.Detail().Task.Revision);
        Assert.Equal("none", Assert.Single(f.Detail().Questions).DeliveryState);
    }

    [Fact]
    public async Task TokenOwnsTaskAndHelperCannotReport()
    {
        using var f = new BoardQueryTests.Fixture();
        var sessions = SessionStore.Open(f.Db, f.Time, "unused");
        var launcher = new FakeLauncher();
        using var hub = new LiveHub(RowStore.Open(f.Db));
        await using var work = new BackgroundWork(f.Time, TextWriter.Null);
        await using var registry = new SessionRegistry(hub, sessions, launcher, f.Time, TextWriter.Null, work);
        var machines = new MachineSet(new FakeMachine(null, sessions));
        registry.Helpers.Delivery.Machines = machines;
        await using var tools = new BoardTools(sessions, registry, f.Transitions, f.Queries, CancellationToken.None);
        await using var app = ApiServer.Build(new ApiServices(hub, sessions, machines, new AttachmentStore("unused"),
            "host-token", registry.Helpers, f.Db.Writer, f.Topics, [new WorkflowCalls(f.Queries, tools.AnswerAsync)], tools), 0, out _);
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        registry.Helpers.Tokens.Url = new Uri(address, "/mcp").ToString();
        var parent = sessions.Create("codex", "project");
        registry.For(parent.SessionId).Send("start");
        var agent = await launcher.NextAsync();
        var opening = await agent.StartAsync();
        var token = opening.GetProperty("params").GetProperty("mcpServers")[0].GetProperty("headers")[0]
            .GetProperty("value").GetString()!["Bearer ".Length..];
        var prompt = await agent.ReadAsync("session/prompt");
        async Task<JsonElement> Request(string method, string parameters, string bearer)
        {
            using var client = new HttpClient { BaseAddress = address };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Content = new StringContent($$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{{parameters}}}""", Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("result").Clone();
        }
        Assert.Contains("bad_args", (await Request("tools/call", """{"name":"board_update"}""", token)).GetRawText());
        var args = JsonSerializer.Serialize(f.Report(), WorkflowJson.Default.BoardUpdateArgs);
        var accepted = await Request("tools/call", """{"name":"board_update","arguments":""" + args + "}", token);
        Assert.False(accepted.GetProperty("isError").GetBoolean());
        foreach (var field in new[] { "creatorSessionId", "sessionId", "role", "machineId", "project" })
        {
            var forged = args[..^1] + "," + JsonSerializer.Serialize(field) + ":" + JsonSerializer.Serialize("forged") + "}";
            Assert.True((await Request("tools/call", """{"name":"board_update","arguments":""" + forged + "}", token))
                .GetProperty("isError").GetBoolean());
        }
        var foreign = sessions.Create("claude", "project");
        Assert.Throws<CallFailed>(() => tools.Update(foreign.SessionId, f.Report()));
        tools.Update(foreign.SessionId, f.Report("foreign"));
        var foreignArgs = JsonSerializer.Serialize(f.Report("foreign"), WorkflowJson.Default.BoardUpdateArgs);
        Assert.Contains("not_owner", (await Request("tools/call",
            """{"name":"board_update","arguments":""" + foreignArgs + "}", token)).GetRawText());
        var helper = sessions.Helper(parent, "claude", "Helper");
        registry.For(helper.SessionId).Send("helper");
        var helperAgent = await launcher.NextAsync();
        var helperOpen = await helperAgent.StartAsync(acpSessionId: "helper");
        var helperToken = helperOpen.GetProperty("params").GetProperty("mcpServers")[0].GetProperty("headers")[0]
            .GetProperty("value").GetString()!["Bearer ".Length..];
        var helperPrompt = await helperAgent.ReadAsync("session/prompt");
        Assert.Contains("helper_forbidden", (await Request("tools/call", """{"name":"board_update","arguments":""" + args + "}", helperToken)).GetRawText());
        var list = await Request("tools/list", "{}", token);
        Assert.Contains(list.GetProperty("tools").EnumerateArray(), x => x.GetProperty("name").GetString() == "start_session");
        var helperList = await Request("tools/call", """{"name":"list_sessions","arguments":{}}""", helperToken);
        Assert.False(helperList.GetProperty("isError").GetBoolean());
        await helperAgent.ReplyAsync(helperPrompt, """{"stopReason":"end_turn"}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await app.StopAsync();
    }
}
