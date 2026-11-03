using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Workspaces;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Socket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Features.Workspaces;

public sealed class WorkspacesSocketTests : IAsyncLifetime
{
    private readonly TempDatabase file = new();
    private AppDatabase db = null!;
    private WebApplication app = null!;
    private Uri address = null!;
    public async Task InitializeAsync()
    {
        var topics = new TopicSet(); db = AppDatabase.Open(file.Path, TimeProvider.System, topics);
        var calls = new SocketDispatcher(db.Writer, topics, TimeProvider.System, NullLogger.Instance);
        new WorkspacesFeature(new WorkspaceStore(db, TimeProvider.System)).Register(calls);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0)); app = builder.Build(); app.UseWebSockets();
        var main = new SocketEndpoint("workspace-test", calls, "actual-source", TimeProvider.System);
        var bulk = new SocketEndpoint("workspace-test", calls, "actual-source", TimeProvider.System, bulk: true);
        app.Map("/socket", main.HandleAsync); app.Map("/socket/bulk", bulk.HandleAsync);
        app.Lifetime.ApplicationStopping.Register(main.CloseAll); app.Lifetime.ApplicationStopping.Register(bulk.CloseAll);
        await app.StartAsync();
        var http = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        address = new Uri($"ws://{http.Authority}/socket");
    }
    public async Task DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); db.Dispose(); file.Dispose(); }

    [Fact]
    public async Task Read_and_save_are_bulk_only_and_named_revision_reaches_the_main_subscription_after_commit()
    {
        using var main = await Open(false); using var bulk = await Open(true);
        var key = new { machine = "actual-source", sessionId = "s", checkoutRoot = "/checkout" };
        var rejected = await Call(main, 1, "read", key);
        Assert.Equal("not_found", rejected.GetProperty("code").GetString());
        var save = new { key.machine, key.sessionId, key.checkoutRoot, expectedRevision = 0, snapshot = new { version = 1, activePath = "é file" } };
        Assert.Equal("not_found", (await Call(main, 2, "save", save)).GetProperty("code").GetString());
        var initial = (await Call(bulk, 3, "read", key)).GetProperty("result");
        Assert.Equal(0, initial.GetProperty("revision").GetInt64()); Assert.Equal(JsonValueKind.Null, initial.GetProperty("snapshot").ValueKind);
        await Send(main, "{\"t\":\"sub\",\"id\":9,\"topic\":\"workspaces\",\"key\":\"actual-source/s//checkout\"}");
        Assert.Empty((await Read(main)).GetProperty("items").EnumerateArray());
        Assert.Equal(1, (await Call(bulk, 4, "save", save)).GetProperty("result").GetProperty("revision").GetInt64());
        var changed = await Read(main);
        Assert.Equal("actual-source/s//checkout", changed.GetProperty("key").GetString());
        Assert.Equal(1, changed.GetProperty("item").GetProperty("revision").GetInt64());
        var restored = (await Call(bulk, 5, "read", key)).GetProperty("result");
        Assert.Equal("é file", restored.GetProperty("snapshot").GetProperty("activePath").GetString());
        Assert.Equal(1, restored.GetProperty("revision").GetInt64());
        Assert.Equal("failed", (await Call(bulk, 6, "save", save)).GetProperty("code").GetString());
        // The next successful commit is a publication barrier; the failed stale write must publish nothing.
        var second = new { key.machine, key.sessionId, key.checkoutRoot, expectedRevision = 1, snapshot = new { version = 1, activePath = "new" } };
        Assert.Equal(2, (await Call(bulk, 7, "save", second)).GetProperty("result").GetProperty("revision").GetInt64());
        Assert.Equal(2, (await Read(main)).GetProperty("item").GetProperty("revision").GetInt64());
    }

    private async Task<ClientWebSocket> Open(bool bulk)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(bulk ? new Uri(address + "/bulk") : address, CancellationToken.None);
        await Send(socket, "{\"t\":\"hello\",\"token\":\"workspace-test\",\"protocol\":1}");
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString()); return socket;
    }
    private static async Task<JsonElement> Call(WebSocket socket, int id, string method, object args)
    {
        await Send(socket, JsonSerializer.Serialize(new { t = "call", id, feature = "workspaces", method, args }));
        return await Read(socket);
    }
    private static Task Send(WebSocket socket, string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Read(WebSocket socket)
    {
        using var message = new MemoryStream(); var bytes = new byte[64 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); WebSocketReceiveResult received;
        do { received = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), timeout.Token); message.Write(bytes, 0, received.Count); }
        while (!received.EndOfMessage);
        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
        using var json = JsonDocument.Parse(message.ToArray()); return json.RootElement.Clone();
    }
}
