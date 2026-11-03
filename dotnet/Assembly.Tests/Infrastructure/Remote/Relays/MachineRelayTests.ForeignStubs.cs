using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Helpers;
using Assembly.Infrastructure.Engine.Relays;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Assembly.Tests.Infrastructure.Remote.Relays;

public sealed partial class MachineRelayTests
{
    [Fact]
    public async Task Mac_MCP_sender_discovers_actual_foreign_recipient_then_existing_relay_delivers_once()
    {
        var delivery = new Delivery();
        await using var h = new TwoHosts(forwardCommands: true, effects: delivery,
            machineLauncher: new RelayTestLauncher(), checkouts: Path.GetTempPath());
        await h.Start();
        const string sender = "mac-sender", recipient = "remote-recipient";
        h.Mac.Fixture.SeedSession(sender); h.Remote.Fixture.SeedSession(recipient);
        var title = new string('é', 40000) + "\0ACTUAL_TITLE😀";
        Assert.True(h.Remote.Fixture.Db.Writer.Write("test.title", scope => h.Remote.Runtime.Commands.ApplyPreparedSessionTitle(scope,
            new(Guid.NewGuid(), h.Remote.Runtime.Machine, new(ActorKind.System, "observer"), recipient, 0, 0, 1000),
            EngineCommands.PrepareSessionTitle(new ReportSessionTitle(title)))).Applied);
        await h.StartMirror();
        var stubs = new ForeignStubs(h.Mac.Runtime, h.Connections, h.Mac.Forwarder!, h.Mac.Fixture.Time);
        var gate = new McpGate(h.Mac.Runtime, h.Mac.Fixture.Limits, h.Mac.Fixture.Time);
        var tool = new SendMessageTool(gate, stubs.EnsureAsync);
        using var input = new MemoryStream(); using var output = new MemoryStream();
        await using var server = McpServer.Create(new StreamServerTransport(input, output), new());
        var request = new RequestContext<CallToolRequestParams>(server,
            new() { Method = "tools/call", Id = new RequestId(1) },
            new()
            {
                Name = "send_message",
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["sessionId"] = JsonSerializer.SerializeToElement(recipient),
                    ["text"] = JsonSerializer.SerializeToElement("Exact body\0[message from copied]")
                }
            })
        { User = new ClaimsPrincipal(new ClaimsIdentity([new("assembly:session", sender), new("assembly:epoch", "0")], "test")) };
        var reply = await tool.InvokeAsync(request);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(reply.Content)).Text;
        Assert.False(reply.IsError, text);
        using var parsed = JsonDocument.Parse(text);
        var id = parsed.RootElement.GetProperty("messageId").GetString()!;
        Assert.Equal("queued", parsed.RootElement.GetProperty("state").GetString());
        Assert.Equal("foreign", h.Mac.Fixture.State("engine_session", recipient));
        Assert.Equal(Encoding.UTF8.GetBytes(title), h.Mac.Fixture.Db.QueryFirst(
            "SELECT CAST(title AS BLOB) FROM engine_session WHERE id=$id", row => row.Blob(0), default, new SqlArg("$id", recipient)));
        Assert.Equal(0, h.Mac.Fixture.Count("engine_outbox"));
        Assert.Equal(0, h.Remote.Fixture.Count("engine_message"));

        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe()
        {
            if (h.Mac.Fixture.State("engine_message", id) == "next_turn") settled.TrySetResult();
        }
        using var committed = h.Mac.Topics.Subscribe("__engine_committed", null, _ => { }, (_, _) => Observe());
        await using var relay = new MachineRelay(h.Mac.Runtime, h.Mac.Forwarder!, h.Connections,
            h.Mac.Topics, h.Mac.Fixture.Time, h.MirrorLog, h.Mac.Needs);
        relay.Start(); Observe();
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, h.Remote.Fixture.Count("engine_message"));
        Assert.Equal(1, h.Mac.Fixture.Count("engine_message"));
        Assert.Single(delivery.Sent);
        Assert.Equal("provider-turn", h.Mac.Fixture.Text("SELECT provider_turn_id FROM engine_message"));
    }
}
