using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Engine;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Infrastructure.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class EngineSocketTests
{
    [Fact]
    public async Task Provider_runtime_queued_state_and_change_keep_the_original_owner_guard_across_actual_replacement()
    {
        await using var fixture = new ProviderRuntimeRemoteFixture();
        await fixture.Start();
        using var observer = await fixture.Open();
        var originalMachine = fixture.RemoteMachine;
        await ProviderRuntimeRemoteFixture.Subscribe(observer, 1, originalMachine);
        await ProviderRuntimeRemoteFixture.Revision(observer, originalMachine, fixture.Remote.Revision);

        using var pair = new SocketPair();
        var calls = new SocketDispatcher(fixture.Hosts.Mac.Fixture.Db.Writer, fixture.Hosts.Mac.Topics, _f.Time, NullLogger.Instance);
        new ProviderRuntimeCalls(fixture.Hosts.Mac.Runtime.Machine, fixture.Local,
            fixture.Hosts.Mac.Forwarder, fixture.Subscriptions).Register(calls);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        calls.Read("probe", "admitted", TestJson.Default.Note, TestJson.Default.Note,
            (note, _) => { admitted.TrySetResult(); return Task.FromResult(note); });
        calls.Read("probe", "finished", TestJson.Default.Note, TestJson.Default.Note,
            (note, _) => { finished.TrySetResult(); return Task.FromResult(note); });
        var connection = new SocketConnection(pair.Server, calls, _f.Time);
        var run = connection.RunAsync(Encoding.UTF8.GetBytes("token"), fixture.Hosts.Mac.Runtime.Machine, default,
            fixture.Hosts.Mac.Runtime.Machine);
        await Send(pair.Client, """{"t":"hello","token":"token","protocol":1}""");
        Assert.Equal("welcome", (await Read(pair.Client)).GetProperty("t").GetString());
        try
        {
            pair.BlockSends = true;
            connection.Send(SocketDispatcher.Frame(new ResultFrame(99, JsonSerializer.SerializeToElement("held", EngineJson.Default.String)), SocketJson.Default.ResultFrame));
            await pair.Entered.Task.WaitAsync(Wait);
            await ProviderRuntimeRemoteFixture.Subscribe(pair.Client, 1, originalMachine);
            await Send(pair.Client, """{"t":"call","id":101,"feature":"probe","method":"admitted","args":{"text":"barrier"}}""");
            await admitted.Task.WaitAsync(Wait); // The real receiver has already queued the old owner's state.

            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notice = fixture.Hosts.Mac.Topics.Subscribe(AgentsFile.Topic, originalMachine, _ => { }, (_, _) => published.TrySetResult());
            var saved = fixture.Save("/fixture/queued-old-owner");
            await published.Task.WaitAsync(Wait); // The real upstream stream also queued an old-owner change.
            notice.Dispose();
            await ProviderRuntimeRemoteFixture.Revision(observer, originalMachine, saved.Revision);
            var original = fixture.Subscriptions.Capture(originalMachine)!;

            await fixture.ReplaceOwner();
            await ProviderRuntimeRemoteFixture.Invalidated(original.Changed);
            await ProviderRuntimeRemoteFixture.Subscribe(observer, 2, fixture.RemoteMachine);
            await ProviderRuntimeRemoteFixture.Revision(observer, fixture.RemoteMachine, fixture.Remote.Revision);
            Assert.Null(fixture.Subscriptions.Capture(originalMachine));
            await Send(pair.Client, """{"t":"call","id":102,"feature":"probe","method":"finished","args":{"text":"end"}}""");
            await finished.Task.WaitAsync(Wait);
            pair.Release.TrySetResult();

            // Both old payloads are ahead of this end marker in the same real sender queue. Neither may leave it.
            var results = new List<long>();
            while (true)
            {
                var frame = await Read(pair.Client);
                Assert.Equal("result", frame.GetProperty("t").GetString());
                var id = frame.GetProperty("id").GetInt64();
                results.Add(id);
                if (id == 102) break;
            }
            Assert.Equal([99L, 101L, 102L], results);
            await pair.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, default);
            await run.WaitAsync(Wait);
        }
        finally { pair.Release.TrySetResult(); }
    }
}
