using System.Text;
using System.Text.Json;
using System.Net.WebSockets;
using Assembly.Engine.Contracts;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Socket;
using Assembly.Engine.Messages;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class EngineSocketTests
{
    [Fact]
    public async Task Owner_prompt_cannot_bypass_the_existing_public_window_allowlist()
    {
        _f.SeedSession();
        using var socket = await Open();
        var result = await Command(socket, new SendOwnerPrompt("s", "Exact text", "C", [], null), "prompt/s/C");
        Assert.False(result.Applied); Assert.Equal("forbidden", result.Error);
        Assert.Equal(0, _f.Count("engine_message")); Assert.Equal(0, _f.Count("engine_outbox"));
        Assert.Equal(0, _f.Count("engine_command_receipt")); Assert.Empty(_f.Events());
    }
    [Fact]
    public async Task Owner_manifest_measurement_matches_actual_registered_wire_with_extreme_ids()
    {
        const long replyId = long.MaxValue;
        var session = "s" + new string('z', 4096) + "\"\\😀";
        _f.SeedSession(session, state: "running");
        var preview = string.Concat(Enumerable.Repeat("😀\"\\\n", 256));
        for (var i = 0; i < 80; i++)
        {
            var command = new SendOwnerPrompt(session, preview, new string('C', 60) + i.ToString("D4"), [], null);
            var prompt = OwnerPromptFingerprint.Prepare(_f.Machine, command, default);
            var result = _f.Db.Writer.Write("test.owner", scope => _runtime.Commands.ApplyOwnerPrompt(scope,
                new(OwnerPromptIdentity.CommandId(_f.Machine, session, command.ClientId), _f.Machine,
                    new(ActorKind.Window, "owner"), OwnerPromptIdentity.MessageId(session, command.ClientId), 0, 0, long.MaxValue), prompt));
            Assert.True(result.Applied, result.Error);
        }
        using var socket = await Open();
        var args = new Assembly.Application.Common.Engine.OwnerPromptReadArgs(session, Limit: 200);
        await Send(socket, $"{{\"t\":\"call\",\"id\":{replyId},\"feature\":\"engine\",\"method\":\"owner_prompts\",\"args\":{JsonSerializer.Serialize(args, EngineSocketJson.Default.OwnerPromptReadArgs)}}}");
        var wire = await OwnerWire(socket);
        using var document = JsonDocument.Parse(wire);
        var frame = document.RootElement;
        Assert.Equal("result", frame.GetProperty("t").GetString());
        var page = frame.GetProperty("result").Deserialize(EngineSocketJson.Default.OwnerPromptPage)!;
        Assert.True(wire.Length <= 65536); Assert.NotEmpty(page.Items); Assert.NotNull(page.NextClientId);
        Assert.Equal(page.Items[^1].ClientId, page.NextClientId);
        Assert.Equal(wire.Length, SocketDispatcher.Result(replyId, frame.GetProperty("result")).Length);
        var local = _runtime.Reader.OwnerPrompts(args, p => SocketDispatcher.Result(replyId,
            JsonSerializer.SerializeToElement(p, EngineSocketJson.Default.OwnerPromptPage)).Length, default);
        Assert.Equal(JsonSerializer.Serialize(local, EngineSocketJson.Default.OwnerPromptPage),
            JsonSerializer.Serialize(page, EngineSocketJson.Default.OwnerPromptPage));
    }

    [Fact]
    public async Task Owner_raw_chunk_uses_existing_bulk_and_actual_one_mib_frame()
    {
        _f.SeedSession(state: "running");
        var text = new string('x', 1024 * 1024 - 1) + "😀";
        var prompt = OwnerPromptFingerprint.Prepare(_f.Machine, new("s", text, "C", [], null), default);
        var result = _f.Db.Writer.Write("test.owner", scope => _runtime.Commands.ApplyOwnerPrompt(scope,
            new(OwnerPromptIdentity.CommandId(_f.Machine, "s", "C"), _f.Machine, new(ActorKind.Window, "owner"), "prompt/s/C", 0, 0, 1000), prompt));
        Assert.True(result.Applied, result.Error);
        var args = new Assembly.Application.Common.Engine.OwnerPromptChunkArgs("s", "C", prompt.ContentSha256, "text", 0, Machine: _f.Machine);
        using var socket = await Open(bulk: true);
        await Send(socket, $"{{\"t\":\"call\",\"id\":{long.MaxValue},\"feature\":\"engine\",\"method\":\"owner_prompt_chunk\",\"args\":{JsonSerializer.Serialize(args, EngineSocketJson.Default.OwnerPromptChunkArgs)}}}");
        var wire = await OwnerWire(socket);
        using var document = JsonDocument.Parse(wire);
        var frame = document.RootElement; Assert.Equal("result", frame.GetProperty("t").GetString());
        var chunk = frame.GetProperty("result").Deserialize(EngineSocketJson.Default.OwnerPromptChunk)!;
        Assert.Equal(1048576, chunk.Data.Length); Assert.Equal(1048576, chunk.NextOffset);
        Assert.InRange(wire.Length, 1398104, 4 * 1024 * 1024);
        Assert.Equal(wire.Length, SocketDispatcher.Result(long.MaxValue, frame.GetProperty("result")).Length);
        Assert.Equal(Encoding.UTF8.GetBytes(text)[..1048576], chunk.Data);
    }

    private static async Task<byte[]> OwnerWire(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(Wait);
        using var buffer = new MemoryStream(); var bytes = new byte[65536];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(bytes, timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType); buffer.Write(bytes, 0, result.Count);
        } while (!result.EndOfMessage);
        return buffer.ToArray();
    }
}
