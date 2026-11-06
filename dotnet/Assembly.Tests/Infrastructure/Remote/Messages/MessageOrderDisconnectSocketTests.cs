using System.Net.WebSockets;
using Assembly.Infrastructure.Engine.Messages;
using Assembly.Tests.Engine.Messages;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class MessageReadSocketTests
{
    [Fact]
    public async Task An_owner_lost_during_registered_name_reads_refuses_collision_but_marks_search_missing()
    {
        await Verify("sender", "sender_order", false);
        await Verify("sender", "match", true);
        await Verify("time", "match", false);

        static async Task Verify(string sort, string heldMethod, bool loseOwner)
        {
            await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts);
            var prefix = new string('n', 256);
            MessageReadFixture.Seed(hosts.Remote.Fixture, "one", 100, "needle", sender: prefix + "a");
            MessageReadFixture.Seed(hosts.Remote.Fixture, "two", 200, "needle", sender: prefix + "z");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            hosts.Remote.AdditionalFeatures = calls => new MessagesCalls(hosts.Remote.Runtime, hosts.Remote.Fixture.Db)
                .Register(new MessageReadGate(calls, async (method, call) =>
                {
                    if (method != heldMethod) return;
                    entered.TrySetResult();
                    await release.Task.WaitAsync(call.Cancellation);
                }));
            await hosts.Start(); await hosts.StartMirror();
            var owner = await hosts.ConnectedMachine.CaptureOwnerBindingAsync(hosts.Remote.Fixture.Machine, default);
            var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = owner.Invalidated.Register(() => invalidated.TrySetResult());
            using var socket = await Open(hosts.Mac, false);
            var search = heldMethod == "match" ? "needle" : "";
            await Call(socket, 1, "page", new { sort, search, limit = 200, machine = hosts.Remote.Fixture.Machine });
            await entered.Task.WaitAsync(Wait);
            if (loseOwner)
            {
                hosts.Remote.Endpoint.DropAll();
                await invalidated.Task.WaitAsync(Wait);
                Assert.True(owner.IsInvalidated);
            }
            else hosts.Remote.BulkEndpoint.DropAll();
            var answer = await Read(socket);
            Assert.Equal(WebSocketState.Open, socket.State);
            if (heldMethod == "sender_order")
            {
                Assert.Equal("error", answer.GetProperty("t").GetString());
                Assert.Equal("unreachable", answer.GetProperty("code").GetString());
                Assert.Equal("workbox-test", answer.GetProperty("machine").GetString());
            }
            else
            {
                var page = Result(answer);
                Assert.Empty(page.GetProperty("items").EnumerateArray());
                Assert.Contains(page.GetProperty("missingMachines").EnumerateArray(), item => item.GetString() == hosts.Remote.Fixture.Machine);
            }
        }
    }
}
