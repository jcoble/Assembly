using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Socket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assembly.Tests.Engine.Delivery;

public sealed partial class AdapterEffectsTests
{
    [Fact]
    public async Task Routed_target_invalid_permission_over_actual_parts_records_bad_answer_and_keeps_live_request()
    {
        await using var f = new AdapterDeliveryFixture();
        var parent = Guid.NewGuid().ToString();
        Assert.True(f.Apply(new RegisterForeignSession("source", "claude", "Parent"), parent, 0,
            new(ActorKind.System, "machine:source")).Applied);
        f.Db.Writer.Write("test.routed-parent", s => s.Execute("UPDATE engine_session SET parent_id=$parent WHERE id=$child",
            new("$parent", parent), new("$child", f.Session.SessionId)));
        var (agent, live, need) = await OpenOwnerPreparationAsk(f, "permission"); using (live)
        {
            await using var service = OwnerNeedService(f);
            await using var machine = new MachineCalls(f.Runtime, f.Time, f.Launcher, Path.GetTempPath(), service);
            var dispatcher = new SocketDispatcher(f.Db.Writer, f.Topics, f.Time, NullLogger.Instance);
            machine.Register(dispatcher);
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            await using var app = builder.Build(); app.UseWebSockets();
            var endpoint = new SocketEndpoint("target-test", dispatcher, f.Runtime.Machine, f.Time, bulk: true, engineMachine: f.Runtime.Machine);
            app.Map("/socket/bulk", endpoint.HandleAsync); app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
            var ownerEndpoint = new SocketEndpoint("target-test", dispatcher, f.Runtime.Machine, f.Time, engineMachine: f.Runtime.Machine);
            app.Map("/socket", ownerEndpoint.HandleAsync); app.Lifetime.ApplicationStopping.Register(ownerEndpoint.CloseAll);
            await app.StartAsync();
            using var socket = new ClientWebSocket(); using var owner = new ClientWebSocket();
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            await socket.ConnectAsync(new("ws://" + address.Authority + "/socket/bulk"), default);
            await socket.SendAsync(Encoding.UTF8.GetBytes("""{"t":"hello","protocol":1,"token":"target-test"}"""), WebSocketMessageType.Text, true, default);
            Assert.Equal("welcome", (await ParentNeedSocketRead(socket)).GetProperty("t").GetString());
            await owner.ConnectAsync(new("ws://" + address.Authority + "/socket"), default);
            await owner.SendAsync(Encoding.UTF8.GetBytes("""{"t":"hello","protocol":1,"token":"target-test"}"""), WebSocketMessageType.Text, true, default);
            Assert.Equal("welcome", (await ParentNeedSocketRead(owner)).GetProperty("t").GetString());
            try
            {
                var source = new ParentNeedSource(Guid.NewGuid(), parent, 3, "source", need, ParentNeedAction.Answer);
                var capturedFrame = await ParentNeedSocketCall(owner, "need_answer_capture", JsonSerializer.SerializeToElement(source, EngineJson.Default.ParentNeedSource));
                var captured = capturedFrame.Deserialize(EngineSocketJson.Default.ParentNeedCaptured)!;
                Assert.NotNull(captured.Binding); Assert.NotNull(captured.Target); Assert.Null(captured.Error);
                var input = new ParentNeedRequest(need, ParentNeedAction.Answer, JsonSerializer.SerializeToElement("missing-option"));
                var normalized = input with { Answer = PreparedParentNeedPhase.NormalizeAnswer(input, captured.Target) };
                var prepared = EngineCommands.PrepareParentNeed(source.OperationId, normalized, captured.Target, captured.Binding, default);
                var descriptor = new NeedAnswerRequestDescriptor(source, captured.Target, prepared.Manifest, captured.Binding);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new ParentNeedPrepared(descriptor, prepared.AnswerUtf8), EngineJson.Default.ParentNeedPrepared);
                var upload = new ParentNeedUploadArgs(ParentNeedUploadKind.Execute, new(bytes.LongLength, RelayHash.Sha(bytes)));
                var opened = (await ParentNeedSocketCall(socket, "need_answer_stage_open", JsonSerializer.SerializeToElement(upload, EngineSocketJson.Default.ParentNeedUploadArgs)))
                    .Deserialize(EngineSocketJson.Default.ParentNeedUploadOpened)!;
                var appended = await ParentNeedSocketCall(socket, "need_answer_stage_part", JsonSerializer.SerializeToElement(
                    new ParentNeedUploadPartArgs(opened.Handle, 0, bytes), EngineSocketJson.Default.ParentNeedUploadPartArgs));
                Assert.Equal(bytes.LongLength, appended.GetProperty("next").GetInt64());
                var ack = (await ParentNeedSocketCall(socket, "need_answer_execute", JsonSerializer.SerializeToElement(new RelayHandleArgs(opened.Handle), EngineSocketJson.Default.RelayHandleArgs)))
                    .Deserialize(EngineSocketJson.Default.NeedAnswerAck)!;
                Assert.Equal("refused", ack.Status); Assert.Equal("bad_answer", ack.Result!.Error);
                Assert.Equal("open", f.State("engine_needs_me", need)); Assert.Null(ack.EffectId);
                Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'", row => row.Long(0), default));
                Assert.NotNull(f.Hosts.For(f.Session.SessionId).PrepareProviderAnswer(need, JsonSerializer.SerializeToElement("yes")));
                var probe = (await ParentNeedSocketCall(owner, "need_answer_probe", JsonSerializer.SerializeToElement(descriptor, EngineSocketJson.Default.NeedAnswerRequestDescriptor)))
                    .Deserialize(EngineSocketJson.Default.NeedAnswerAck)!;
                Assert.Equal(ack, probe);
            }
            finally
            {
                socket.Abort(); owner.Abort(); await service.StopWaiting(); await app.StopAsync();
                await CloseOwnerPreparationAsk(f, agent, need, false);
            }
        }
    }

    private static async Task<JsonElement> ParentNeedSocketCall(WebSocket socket, string method, JsonElement args)
    {
        var json = JsonSerializer.Serialize(new { t = "call", id = 17, feature = "engine", method, args });
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, default);
        var reply = await ParentNeedSocketRead(socket);
        Assert.True(reply.GetProperty("t").GetString() == "result", method + ": " + reply.GetRawText());
        return reply.GetProperty("result").Clone();
    }
    private static async Task<JsonElement> ParentNeedSocketRead(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var output = new MemoryStream(); var buffer = new byte[65536]; WebSocketReceiveResult part;
        do { part = await socket.ReceiveAsync(buffer, timeout.Token); output.Write(buffer, 0, part.Count); } while (!part.EndOfMessage);
        using var json = JsonDocument.Parse(output.ToArray()); return json.RootElement.Clone();
    }
}
