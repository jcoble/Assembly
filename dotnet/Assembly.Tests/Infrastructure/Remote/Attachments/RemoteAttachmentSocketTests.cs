using Assembly.Application.Common.Images;
using System.Net;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Reading;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Remote.Attachments;

public sealed class RemoteAttachmentSocketTests
{
    [Fact]
    public async Task Remote_image_upload_keeps_all_bytes_with_only_existing_socket_routes()
    {
        var home = Directory.CreateTempSubdirectory("assembly-upload-socket-").FullName;
        try
        {
            var topics = new Topics();
            using var f = new EngineFixture(topics: topics);
            var rows = RowStore.Open(f.Db);
            using var hub = new LiveHub(rows);
            var sessions = SessionStore.Open(f.Db, f.Time, Path.Combine(home, "sessions"));
            var session = sessions.Create("codex", "repo");
            var local = new FakeMachine(null, sessions);
            var staging = Path.Combine(home, "staging");
            var calls = new SocketDispatcher(f.Db.Writer, topics, f.Time, NullLogger.Instance);
            new AttachmentsFeature(sessions, new MachineSet(local), staging).Register(calls);
            calls.Read("engine", "machine", EngineSocketJson.Default.JsonElement, EngineSocketJson.Default.MachineInfo,
                (_, _) => Task.FromResult(new MachineInfo(f.Machine, 0)));
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
            await using var app = builder.Build();
            app.UseWebSockets();
            var socket = new SocketEndpoint("token", calls, "workbox-test", f.Time, engineMachine: f.Machine);
            var bulk = new SocketEndpoint("token", calls, "workbox-test", f.Time, bulk: true, engineMachine: f.Machine);
            app.Map("/socket", socket.HandleAsync);
            app.Map("/socket/bulk", bulk.HandleAsync);
            app.Lifetime.ApplicationStopping.Register(socket.CloseAll);
            app.Lifetime.ApplicationStopping.Register(bulk.CloseAll);
            await app.StartAsync();
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            await using var remote = new RemoteMachine("workbox-test", address, "token", hub, sessions, TextWriter.Null,
                new MachineStatus(topics, ["workbox-test"]));
            await remote.ProbeAsync(default);
            await remote.ReadMirrorOwnerAsync(default);
            var type = ImageType.ForExtension("png")!;
            var bytes = new byte[5 * 1024 * 1024 / 2];
            new Random(17).NextBytes(bytes);
            type.Magic.CopyTo(bytes, 0);
            var path = await remote.SaveAttachmentAsync(session, type, bytes, default);
            Assert.False(string.IsNullOrWhiteSpace(path));
            Assert.Equal(bytes, local.Saved);
            Assert.Empty(Directory.GetFiles(staging));
        }
        finally { Directory.Delete(home, true); }
    }
}
