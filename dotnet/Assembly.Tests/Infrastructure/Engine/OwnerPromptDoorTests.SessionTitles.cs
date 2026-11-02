using System.Net;
using System.Net.Http.Headers;
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
        using var hub = new LiveHub(RowStore.Open(h.Fixture.Db));
        await using var app = ApiServer.Build(new(hub, h.Sessions, new MachineSet(h.Machine), h.Attachments, "owner-token", OwnerDoor: h.Door), 0, out _);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var route = $"/sessions/{id}/title";
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(route)).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "owner-token");
        using var legacy = JsonDocument.Parse(await http.GetStringAsync($"/sessions/{id}"));
        Assert.Equal(title, legacy.RootElement.GetProperty("title").GetString());
        var stat = JsonSerializer.Deserialize(await http.GetByteArrayAsync(route), SessionsJson.Default.SessionTitleStat)!;
        Assert.Equal(h.Fixture.Machine, stat.EngineMachine);
        var pinned = $"{route}/content?version={stat.Version}&epoch={stat.Epoch}&totalBytes={stat.Utf8Bytes}&machine={stat.EngineMachine}";
        using var all = new MemoryStream(); long offset = 0;
        while (true)
        {
            var wire = await http.GetByteArrayAsync(pinned + $"&offset={offset}");
            var chunk = JsonSerializer.Deserialize(wire, SessionsJson.Default.SessionTitleChunk)!;
            Assert.InRange(wire.Length, 1, 4 * 1024 * 1024);
            Assert.Equal(wire.Length, JsonReply.Encode(json => JsonSerializer.Serialize(json, chunk, SessionsJson.Default.SessionTitleChunk)).Length);
            all.Write(chunk.Data);
            if (chunk.NextOffset is not { } next) break;
            offset = next;
        }
        Assert.Equal(title, new UTF8Encoding(false, true).GetString(all.ToArray()));
        Assert.Equal(HttpStatusCode.Conflict, (await http.GetAsync(route + "?machine=paired-phone-id")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/sessions/unknown/title")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync(pinned + "&count=1048577")).StatusCode);
        Assert.True(h.Fixture.Apply(new ReportCapabilities(true), id, stat.Version, actor: new(ActorKind.System, "test")).Applied);
        Assert.Equal(HttpStatusCode.Conflict, (await http.GetAsync(pinned)).StatusCode);
        Assert.Empty(h.Machine.Asked);
    }
}
