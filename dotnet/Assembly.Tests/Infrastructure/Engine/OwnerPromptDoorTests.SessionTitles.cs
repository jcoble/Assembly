using System.Net.WebSockets;
using Assembly.Tests.Infrastructure.Http;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Assembly.Tests.Infrastructure.Engine;

public sealed partial class OwnerPromptDoorTests
{
    [Fact]
    public async Task Actual_optional_http_title_routes_authorize_reopen_and_pin_exact_mutable_bytes()
    {
        await using var h = new Harness();
        var session = h.Sessions.Create("codex", "repo"); var id = session.SessionId;
        h.Fixture.SeedSession(id);
        var title = "\0" + new string('x', 1048575) + "😀\0suffix";
        h.Sessions.SetTitle(id, title, "user");
        var context = new CommandContext(Guid.NewGuid(), h.Fixture.Machine, new(ActorKind.System, "test"), id, 0, 0, 1000);
        Assert.True(h.Fixture.Db.Writer.Write("title", s => new EngineCommands(h.Fixture.Machine, h.Fixture.Limits)
            .ApplyPreparedSessionTitle(s, context, EngineCommands.PrepareSessionTitle(new ReportSessionTitle(title)))).Applied);
        await h.Reopen();
        await using var app = ApiServer.Build(h.SocketServices, 0, out _);
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        await OwnerDeniedAsync(address);
        using var socket = await ApiTests.OpenOwnerSocketAsync(address, "owner-token");
        using var bulk = await ApiTests.OpenOwnerSocketAsync(address, "owner-token", "/socket/bulk");
        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { t = "sub", id = 2, topic = "sessions", key = id })).AsMemory(),
            WebSocketMessageType.Text, true, CancellationToken.None);
        var legacy = await ApiTests.ReadFrameAsync(socket);
        Assert.Equal("state", legacy.GetProperty("t").GetString());
        Assert.Equal(title, Assert.Single(legacy.GetProperty("items").EnumerateArray()).GetProperty("item").GetProperty("title").GetString());
        var stat = ApiTests.Result(await OwnerCallAsync(socket, "session_title", new { session = id }))
            .Deserialize(SessionsJson.Default.SessionTitleStat)!;
        Assert.Equal(h.Fixture.Machine, stat.EngineMachine);
        using var all = new MemoryStream(); long offset = 0;
        while (true)
        {
            var result = ApiTests.Result(await OwnerCallAsync(bulk, "session_title_chunk",
                new { session = id, version = stat.Version, epoch = stat.Epoch, totalBytes = stat.Utf8Bytes, machine = stat.EngineMachine, offset }));
            var wire = Encoding.UTF8.GetBytes(result.GetRawText());
            var chunk = result.Deserialize(SessionsJson.Default.SessionTitleChunk)!;
            Assert.InRange(wire.Length, 1, 4 * 1024 * 1024);
            Assert.Equal(wire.Length, JsonReply.Encode(json => JsonSerializer.Serialize(json, chunk, SessionsJson.Default.SessionTitleChunk)).Length);
            all.Write(chunk.Data);
            if (chunk.NextOffset is not { } next) break;
            offset = next;
        }
        Assert.Equal(title, new UTF8Encoding(false, true).GetString(all.ToArray()));
        await OwnerErrorAsync(socket, "session_title", new { session = id, machine = "paired-phone-id" }, "not_found");
        await OwnerErrorAsync(socket, "session_title", new { session = "unknown" }, "not_found");
        await OwnerErrorAsync(bulk, "session_title_chunk", new
        {
            session = id,
            version = stat.Version,
            epoch = stat.Epoch,
            totalBytes = stat.Utf8Bytes,
            machine = stat.EngineMachine,
            offset = 0,
            count = 1048577
        }, "failed");
        Assert.True(h.Fixture.Apply(new ReportCapabilities(true), id, stat.Version, actor: new(ActorKind.System, "test")).Applied);
        await OwnerErrorAsync(bulk, "session_title_chunk", new
        {
            session = id,
            version = stat.Version,
            epoch = stat.Epoch,
            totalBytes = stat.Utf8Bytes,
            machine = stat.EngineMachine,
            offset = 0
        }, "failed");
        Assert.Empty(h.Machine.Asked);
    }
}
