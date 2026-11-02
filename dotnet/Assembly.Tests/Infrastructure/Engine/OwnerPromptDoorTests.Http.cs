using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Messages;
using Assembly.Infrastructure.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Assembly.Tests.Infrastructure.Engine;

public sealed partial class OwnerPromptDoorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(7L)]
    public async Task Actual_owner_http_reopen_reconstructs_original_with_engine_identity_and_authorization(long? epoch)
    {
        await using var h = new Harness();
        var session = h.Sessions.Create("codex", "repo"); var id = session.SessionId;
        h.Fixture.SeedSession(id, state: "running", epoch: 7);
        var text = "\0" + new string('a', 2 * 1024 * 1024) + "😀\0end";
        var reference = h.Attachments.PathOf(id, $"{Guid.NewGuid()}.png");
        var request = new OwnerPromptRequest(id, text, "C", [reference], epoch);
        var accepted = h.Fixture.Db.Writer.Write("send/send", await h.Door.PrepareOwnerPromptAsync(request, default));
        await h.Reopen();
        using var hub = new LiveHub(RowStore.Open(h.Fixture.Db));
        await using var app = ApiServer.Build(new(hub, h.Sessions, new MachineSet(h.Machine), h.Attachments, "owner-token", OwnerDoor: h.Door), 0, out _);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var route = $"/sessions/{id}/owner-prompts";
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(route)).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "owner-token");
        var pageBytes = await http.GetByteArrayAsync(route);
        Assert.True(pageBytes.Length <= 65536);
        var page = JsonSerializer.Deserialize(pageBytes, SessionsJson.Default.OwnerPromptPage)!;
        Assert.Equal(h.Fixture.Machine, page.EngineMachine); Assert.NotEqual("paired-phone-id", page.EngineMachine);
        var manifest = Assert.Single(page.Items); Assert.False(manifest.TextComplete); Assert.Equal(epoch, manifest.TargetEpoch);
        Assert.Equal(pageBytes.Length, JsonReply.Encode(json => JsonSerializer.Serialize(json, page, SessionsJson.Default.OwnerPromptPage)).Length);
        async Task<string> Part(string part)
        {
            using var all = new MemoryStream(); long offset = 0;
            while (true)
            {
                var uri = $"{route}/C/content?part={part}&offset={offset}&contentSha256={manifest.ContentSha256}&machine={page.EngineMachine}";
                var chunk = JsonSerializer.Deserialize(await http.GetByteArrayAsync(uri), SessionsJson.Default.OwnerPromptChunk)!;
                all.Write(chunk.Data);
                if (chunk.NextOffset is not { } next) break;
                offset = next;
            }
            return new UTF8Encoding(false, true).GetString(all.ToArray());
        }
        var exactText = await Part("text"); var references = JsonSerializer.Deserialize(await Part("attachments"), SessionsJson.Default.IReadOnlyListString)!;
        Assert.Equal(text, exactText); Assert.Equal(request.AttachmentIds, references);
        var full = OwnerPromptFingerprint.Prepare(page.EngineMachine, new(id, exactText, manifest.ClientId, references, manifest.TargetEpoch), default);
        Assert.Equal(manifest.ContentSha256, full.ContentSha256);
        Assert.NotEqual(full.ContentSha256, OwnerPromptFingerprint.Prepare("paired-phone-id", new(id, exactText, manifest.ClientId, references, manifest.TargetEpoch), default).ContentSha256);
        Assert.Equal(accepted, h.Fixture.Db.Writer.Write("send/send", await h.Door.PrepareOwnerPromptAsync(new(id, exactText, "C", references, manifest.TargetEpoch), default)));
        Assert.Equal(HttpStatusCode.Conflict, (await http.GetAsync(route + "?machine=paired-phone-id")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/sessions/{id}/owner-prompts/other/content?part=text&contentSha256={manifest.ContentSha256}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.GetAsync(route + "/C/content?part=text&contentSha256=" + new string('a', 64))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync(route + "?limit=invalid")).StatusCode);
        Assert.Empty(h.Machine.Asked); Assert.Equal(1, h.Fixture.Count("engine_message"));
    }
}
