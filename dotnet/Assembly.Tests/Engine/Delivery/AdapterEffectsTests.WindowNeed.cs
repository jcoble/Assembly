using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Engine.NeedsMe;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Machines;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Assembly.Tests.Engine.Delivery;

public sealed partial class AdapterEffectsTests
{
    [Theory]
    [InlineData("permission")]
    [InlineData("question")]
    public async Task Registered_window_answer_prepares_original_provider_question_and_replays_once(string kind)
    {
        await using var f = new AdapterDeliveryFixture();
        var (agent, live, need) = await OpenOwnerPreparationAsk(f, kind);
        using (live)
        {
            var attachments = new AttachmentStore(Path.GetTempPath());
            var local = new LocalMachine(f.Hub, f.Sessions, f.Hosts, attachments);
            await using var needs = OwnerNeedService(f);
            await using var app = ApiServer.Build(new(f.Hub, f.Sessions, new MachineSet(local), attachments, "isolated-token",
                Writer: f.Db.Writer, Topics: f.Topics, Features: [new EngineCalls(f.Runtime, f.Time, needs)], Engine: f.Runtime), 0, out _);
            var responseRead = false;
            try
            {
                await app.StartAsync();
                var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new($"ws://{address.Authority}/socket"), default);
                await SocketSend(socket, """{"t":"hello","token":"isolated-token","protocol":1}""");
                Assert.Equal("welcome", (await SocketRead(socket)).GetProperty("t").GetString());
                var answer = kind == "permission" ? JsonSerializer.SerializeToElement("yes")
                    : JsonSerializer.SerializeToElement(new { action = "accept", content = new { body = "Owner response λ", blank = "" } });
                var window = OwnerWindow(f, need, answer);
                var args = JsonSerializer.Serialize(window, EngineSocketJson.Default.WindowCommand);
                var frame = $$"""{"t":"call","id":1,"feature":"engine","method":"command","args":{{args}}}""";
                await SocketSend(socket, frame);
                var accepted = (await SocketRead(socket)).GetProperty("result").Clone();
                Assert.True(accepted.GetProperty("applied").GetBoolean(), accepted.GetRawText());
                Assert.NotNull(f.Hosts.For(f.Session.SessionId).PrepareProviderAnswer(need, answer));
                Assert.Equal("answered", f.State("engine_needs_me", need));
                var effect = f.ClaimEffect("answer/" + need);
                var receipt = await f.Effects.ExecuteAsync(effect, default);
                var wire = await agent.NextMessageAsync().WaitAsync(DeliveryWait); responseRead = true;
                Assert.Equal(7, wire.GetProperty("id").GetInt32());
                if (kind == "permission") Assert.Equal("yes", wire.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
                else Assert.True(JsonElement.DeepEquals(answer, wire.GetProperty("result")));
                Assert.True(f.Complete(effect, receipt).Applied);
                await AskRow(live, "answered");
                await SocketSend(socket, frame);
                Assert.Equal(accepted.GetRawText(), (await SocketRead(socket)).GetProperty("result").GetRawText());
                Assert.Equal(1, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE id=$id", r => r.Long(0), default, new SqlArg("$id", "answer/" + need)));
            }
            finally
            {
                await app.StopAsync();
                await CloseOwnerPreparationAsk(f, agent, need, responseRead);
            }
        }
    }
}
