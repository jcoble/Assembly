using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Engine.Persistence;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class MachineDoorTests
{
    private static ForeignAncestryArgs AncestryArgs() => new("source", "ancestor-parent",
        [new("ancestor-root", "source", "custom", "Root", null),
         new("ancestor-parent", "source", "custom", "Parent", "ancestor-root")]);

    private static async Task<JsonElement> Ancestry(WebSocket socket, ForeignAncestryArgs args)
    {
        var json = JsonSerializer.Serialize(args, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await Send(socket, "{\"t\":\"call\",\"id\":21,\"feature\":\"engine\",\"method\":\"foreign_ancestry\",\"args\":" + json + "}");
        return await Read(socket);
    }

    [Fact]
    public async Task Owner_socket_builds_and_replays_invisible_foreign_ancestry()
    {
        using var socket = await Open();
        Assert.True(Result(await Ancestry(socket, AncestryArgs())).Applied);
        Assert.True(Result(await Ancestry(socket, AncestryArgs())).Applied);
        Assert.Equal(2, _f.Count("engine_session"));
        Assert.Equal(2, _f.Count("engine_command_receipt"));
        Assert.Equal(0, _f.Number("SELECT SUM(version+managed_checkout) FROM engine_session"));
        Assert.Equal("ancestor-root", _f.Text("SELECT parent_id FROM engine_session WHERE id='ancestor-parent'"));
        Assert.Equal(0, _f.Number("SELECT COUNT(*) FROM session_info"));
        Assert.Empty(_f.Events());
        Assert.Equal(0, _f.Count("engine_outbox"));
    }

    [Fact]
    public async Task Missing_and_phone_peers_cannot_create_private_parent_markers()
    {
        using var socket = await Open();
        foreach (var peer in new CallPeer?[] { null, new PhonePeer("device") })
        {
            _overridePeer = true; _peer = peer;
            var frame = await Ancestry(socket, AncestryArgs());
            Assert.Equal("error", frame.GetProperty("t").GetString());
            Assert.Equal("forbidden", frame.GetProperty("message").GetString());
        }
        Assert.Equal(0, _f.Count("engine_session"));
        Assert.Equal(0, _f.Count("engine_command_receipt"));
    }

    [Fact]
    public async Task Typed_refusal_escapes_real_writer_and_preserves_exact_wire_reason()
    {
        using var socket = await Open();
        _f.Db.Writer.Write("test.inject-conflicting-reference", scope => scope.Execute("""
            CREATE TRIGGER replace_second_reference AFTER INSERT ON engine_session WHEN NEW.id='ancestor-root'
            BEGIN
                INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,folder,title,state,
                    activity_state,holder,epoch,cancel_requested,account_context,managed_checkout)
                VALUES('ancestor-parent',0,'other',0,0,'custom','','Unexpected','foreign','idle','other',0,0,'',0);
            END
            """));
        var publications = 0;
        using var subscription = _topics.Subscribe("__engine_committed", null, _ => { }, (_, _) => Interlocked.Increment(ref publications));
        var frame = await Ancestry(socket, AncestryArgs());
        Assert.Equal("error", frame.GetProperty("t").GetString());
        Assert.Equal("failed", frame.GetProperty("code").GetString());
        Assert.Equal("forbidden", frame.GetProperty("message").GetString());
        Assert.Equal(0, _f.Count("engine_session"));
        Assert.Equal(0, _f.Count("engine_command_receipt"));
        Assert.Equal(0, _f.Number("SELECT COUNT(*) FROM session_info"));
        Assert.Empty(_f.Events());
        Assert.Equal(0, _f.Count("engine_outbox"));
        Assert.Equal(0, publications);
    }
}
