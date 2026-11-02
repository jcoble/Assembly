using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Engine.Messages;
using Assembly.Engine.Reading;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Engine.Relays;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class MachineDoorTests
{
    private async Task<ClientWebSocket> OpenBulk()
    {
        var socket = new ClientWebSocket(); await socket.ConnectAsync(new Uri(_address + "/bulk"), default);
        await Send(socket, """{"t":"hello","protocol":1,"token":"token"}""");
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString()); return socket;
    }
    private static async Task<JsonElement> RelayCall<T>(WebSocket socket, string method, T args, JsonTypeInfo<T> type)
    {
        await Send(socket, "{\"t\":\"call\",\"id\":11,\"feature\":\"engine\",\"method\":\"" + method + "\",\"args\":" + JsonSerializer.Serialize(args, type) + "}");
        return await Read(socket);
    }

    [Fact]
    public async Task Registered_source_parts_read_actual_full_owning_bytes_and_detect_changed_fence()
    {
        _f.SeedSession("sender");
        Assert.True(_f.Apply(new RegisterForeignSession("B", "codex", "foreign"), "recipient",
            actor: new(Assembly.Engine.Contracts.ActorKind.System, "machine:B")).Applied);
        var title = new string('界', 1500000) + "\0CAPTURED_LABEL_SUFFIX";
        Assert.True(_f.Db.Writer.Write("title", scope => _runtime.Commands.ApplyPreparedSessionTitle(scope,
            new(Guid.NewGuid(), _runtime.Machine, new(Assembly.Engine.Contracts.ActorKind.System, "observer"), "sender", 0, 0, 1000),
            Assembly.Engine.EngineCommands.PrepareSessionTitle(new ReportSessionTitle(title)))).Applied);
        var body = new string('x', 4 * 1024 * 1024 + 17) + "\0TASK_SUFFIX😀";
        var prompt = PreparedHelperPrompt.Prepare(new("sender", "recipient", body, "prompt"), title, 0);
        Assert.True(_f.Db.Writer.Write("source", scope => _runtime.Commands.ApplyPreparedHelperPrompt(scope,
            new(Guid.NewGuid(), _runtime.Machine, new(Assembly.Engine.Contracts.ActorKind.Session, "sender"), "message", 0, 0, 1000), prompt)).Applied);
        using var socket = await OpenBulk();
        var frame = await RelayCall(socket, "relay_source_open", new("message", "B"), EngineSocketJson.Default.RelaySourceReadArgs);
        var opened = frame.GetProperty("result").Deserialize(EngineSocketJson.Default.RelaySourceOpened)!;
        foreach (var (kind, expected) in new[] { (RelayPartKind.Body, body), (RelayPartKind.SenderLabel, MessageText.Label(title, "sender", "session")) })
        {
            using var actual = new MemoryStream(); long offset = 0;
            while (offset < (kind == RelayPartKind.Body ? opened.Source.Body.Utf8Bytes : opened.Source.SenderLabel.Utf8Bytes))
            {
                var partFrame = await RelayCall(socket, "relay_source_part", new(opened.Handle, kind, offset), EngineSocketJson.Default.RelaySourcePartArgs);
                var part = partFrame.GetProperty("result").Deserialize(EngineSocketJson.Default.RelaySourcePart)!;
                actual.Write(part.Data); Assert.Equal(offset, part.Offset); offset = part.Next;
            }
            Assert.Equal(Encoding.UTF8.GetBytes(expected), actual.ToArray());
        }
        Assert.Equal("result", (await RelayCall(socket, "relay_source_revalidate", new(opened.Handle), EngineSocketJson.Default.RelayHandleArgs)).GetProperty("t").GetString());
        _f.Db.Writer.Write("change-fence", scope => scope.Execute("UPDATE engine_message SET version=version+1 WHERE id='message'"));
        var changed = await RelayCall(socket, "relay_source_revalidate", new(opened.Handle), EngineSocketJson.Default.RelayHandleArgs);
        Assert.Equal("error", changed.GetProperty("t").GetString()); Assert.Contains("source_changed", changed.GetProperty("message").GetString());
        Assert.Equal("result", (await RelayCall(socket, "relay_source_close", new(opened.Handle), EngineSocketJson.Default.RelayHandleArgs)).GetProperty("t").GetString());
        Assert.Equal("error", (await RelayCall(socket, "relay_source_part", new(opened.Handle, RelayPartKind.Body, 0), EngineSocketJson.Default.RelaySourcePartArgs)).GetProperty("t").GetString());
    }

    [Fact]
    public async Task Registered_bulk_target_preserves_oversized_body_and_label_and_replays_without_parts()
    {
        using var source = RelaySourceTests.Source(4 * 1024 * 1024 + 25, out var text, out var label, _runtime.Machine, 1500000);
        var descriptor = RelayDigests.Describe(new EngineReader(source.Db, source.Machine).RelaySource(new("message", _runtime.Machine), default));
        _f.SeedSession("recipient");
        Assert.True(_f.Apply(new RegisterForeignSession(source.Machine, "codex", "changed target stub"), "sender",
            actor: new(Assembly.Engine.Contracts.ActorKind.System, "machine:" + source.Machine)).Applied);
        using var socket = await OpenBulk(); var request = new RelaySendRequest(descriptor, 0);
        var frame = await RelayCall(socket, "relay_send_begin", request, EngineSocketJson.Default.RelaySendRequest);
        Assert.Equal("result", frame.GetProperty("t").GetString());
        var begin = frame.GetProperty("result").Deserialize(EngineSocketJson.Default.RelaySendBeginReply)!;
        Assert.Equal("parts_required", begin.Status);
        foreach (var (kind, bytes) in new[] { (RelayPartKind.Body, Encoding.UTF8.GetBytes(text)), (RelayPartKind.SenderLabel, Encoding.UTF8.GetBytes(label)) })
            for (var offset = 0; offset < bytes.Length; offset += RelaySegments.ChunkBytes)
            {
                var data = bytes.AsSpan(offset, Math.Min(RelaySegments.ChunkBytes, bytes.Length - offset)).ToArray();
                var part = await RelayCall(socket, "relay_send_part", new(begin.Transfer!, kind, offset, data), EngineSocketJson.Default.RelaySendPartArgs);
                Assert.Equal("result", part.GetProperty("t").GetString());
            }
        var committed = await RelayCall(socket, "relay_send_commit", new(request, begin.Transfer), EngineSocketJson.Default.RelaySendCommitArgs);
        Assert.Equal("result", committed.GetProperty("t").GetString());
        var ack = committed.GetProperty("result").Deserialize(EngineSocketJson.Default.RelaySendAck)!;
        Assert.True(ack.First!.Applied); Assert.True(ack.DestinationPresent); Assert.False(ack.Unknown);
        Assert.Equal(Encoding.UTF8.GetBytes(text), _f.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message WHERE id='message'", r => r.Blob(0), default));
        Assert.Equal(Encoding.UTF8.GetBytes(label), _f.Db.QueryFirst("SELECT CAST(sender_label AS BLOB) FROM engine_message WHERE id='message'", r => r.Blob(0), default));
        var replay = await RelayCall(socket, "relay_send_begin", request, EngineSocketJson.Default.RelaySendRequest);
        var recorded = replay.GetProperty("result").Deserialize(EngineSocketJson.Default.RelaySendBeginReply)!;
        Assert.Equal("recorded", recorded.Status); Assert.Null(recorded.Transfer); Assert.Equal(1, _f.Count("engine_message"));
    }

    [Fact]
    public async Task Relay_bulk_methods_refuse_unauthenticated_roles_and_normal_socket()
    {
        using var socket = await OpenBulk();
        foreach (var peer in new CallPeer?[] { null, new PhonePeer("device") })
        {
            _overridePeer = true; _peer = peer;
            var refused = await RelayCall(socket, "relay_source_open", new("message", "B"), EngineSocketJson.Default.RelaySourceReadArgs);
            Assert.Equal("error", refused.GetProperty("t").GetString());
            Assert.Contains("forbidden", refused.GetProperty("message").GetString());
        }
        _overridePeer = false;
        using var normal = await Open();
        Assert.Equal("error", (await RelayCall(normal, "relay_source_open", new("message", "B"), EngineSocketJson.Default.RelaySourceReadArgs)).GetProperty("t").GetString());
        Assert.Equal(0, _f.Count("engine_message"));
    }
}
