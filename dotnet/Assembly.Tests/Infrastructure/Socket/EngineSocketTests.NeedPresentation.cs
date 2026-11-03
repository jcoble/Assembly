using System.Text.Json;
using System.Text.Json.Nodes;
using Assembly.Application.Common.Calls;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class EngineSocketTests
{
    [Fact]
    public async Task Actual_phone_peer_cannot_route_or_supply_surface_and_records_only_its_authenticated_device()
    {
        _f.SeedSession("s");
        Assert.True(_f.Apply(new OpenNeed("s", null, null, "question", "Recorded question", [], null, null, null),
            "phone-need", actor: new(ActorKind.System, "host")).Applied);
        _overrideTitlePeer = true; _titlePeer = new PhonePeer("actual-device");
        using var socket = await Open();
        var command = new WindowCommand(Guid.NewGuid(), "phone-need", 1, 0, new ResolveNeed("dismissed", null), "remote-machine");
        Assert.Equal("forbidden", (await NeedCommand(socket, command)).Error);
        Assert.Equal(0, _forwardAttempts);
        Assert.Equal("bad_args", (await NeedCommand(socket, command with { Machine = null, Surface = "board" })).Error);
        Assert.Equal("open", _f.State("engine_needs_me", "phone-need"));
        Assert.True((await NeedCommand(socket, command with { Machine = null })).Applied);
        using var origin = JsonDocument.Parse(_f.Text("SELECT answered_origin_json FROM engine_needs_me")!);
        Assert.Equal("phone", origin.RootElement.GetProperty("channel").GetString());
        Assert.Equal("actual-device", origin.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal(JsonValueKind.Null, origin.RootElement.GetProperty("surface").ValueKind);
        Assert.Equal("owner", _f.Text("SELECT answered_by FROM engine_needs_me"));
    }

    [Fact]
    public async Task Window_surface_is_display_only_and_extra_origin_or_actor_cannot_forge_phone_provenance()
    {
        _f.SeedSession("s");
        Assert.True(_f.Apply(new OpenNeed("s", null, null, "question", "Recorded question", [], null, null, null),
            "desktop-need", actor: new(ActorKind.System, "host")).Applied);
        _overrideTitlePeer = true; _titlePeer = new OwnerPeer();
        using var socket = await Open();
        var command = new WindowCommand(Guid.NewGuid(), "desktop-need", 1, 0, new ResolveNeed("dismissed", null), Surface: "phone");
        Assert.Equal("bad_args", (await NeedCommand(socket, command)).Error);
        var eventCount = _f.Events().Count;
        var receiptCount = _f.Count("engine_command_receipt");
        var effectCount = _f.Count("engine_outbox");
        var args = JsonSerializer.SerializeToNode(command with { Surface = "board" }, EngineSocketJson.Default.WindowCommand)!.AsObject();
        args["origin"] = JsonNode.Parse("""{"channel":"phone","deviceId":"forged","surface":"rail"}""");
        args["actor"] = JsonNode.Parse("""{"kind":"system","id":"forged"}""");
        await Send(socket, $$"""{"t":"call","id":11,"feature":"engine","method":"command","args":{{args.ToJsonString()}}}""");
        var reply = await Read(socket);
        Assert.Equal("error", reply.GetProperty("t").GetString());
        Assert.Equal("bad_args", reply.GetProperty("code").GetString());
        Assert.Equal("open", _f.State("engine_needs_me", "desktop-need"));
        Assert.Equal(1, _f.Number("SELECT version FROM engine_needs_me"));
        Assert.Null(_f.Text("SELECT answered_origin_json FROM engine_needs_me"));
        Assert.Null(_f.Text("SELECT answered_by FROM engine_needs_me"));
        Assert.Equal(eventCount, _f.Events().Count);
        Assert.Equal(receiptCount, _f.Count("engine_command_receipt"));
        Assert.Equal(effectCount, _f.Count("engine_outbox"));
        Assert.True((await NeedCommand(socket, command with { Surface = "board" })).Applied);
        using var origin = JsonDocument.Parse(_f.Text("SELECT answered_origin_json FROM engine_needs_me")!);
        Assert.Equal("desktop", origin.RootElement.GetProperty("channel").GetString());
        Assert.Equal("board", origin.RootElement.GetProperty("surface").GetString());
        Assert.Equal(JsonValueKind.Null, origin.RootElement.GetProperty("deviceId").ValueKind);
        Assert.Equal("owner", _f.Text("SELECT answered_by FROM engine_needs_me"));
    }

    private async Task<CommandResult> NeedCommand(System.Net.WebSockets.WebSocket socket, WindowCommand command)
    {
        var args = JsonSerializer.Serialize(command, EngineSocketJson.Default.WindowCommand);
        await Send(socket, $$"""{"t":"call","id":11,"feature":"engine","method":"command","args":{{args}}}""");
        var reply = await Read(socket);
        Assert.Equal("result", reply.GetProperty("t").GetString());
        return reply.GetProperty("result").Deserialize(EngineJson.Default.CommandResult)!;
    }
}
