using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Socket;
using Assembly.Infrastructure.Topics;
using Assembly.Application.Common.Acp;
using Assembly.Tests.Draft.Base.Chat;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class RemoteMachineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Composer_transport_catalogue_near_and_over_socket_limit_is_complete_or_failed_and_owned_PID_exits(bool oversized)
    {
        const int limit = 4 * 1024 * 1024;
        var length = limit + (oversized ? 4096 : -4096);
        AgentConnection? adapter = null;
        const string script = """
            $| = 1;
            while (<STDIN>) {
                my ($id) = /"id":(\d+)/;
                print qq({"jsonrpc":"2.0","id":$id,"result":{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[]}}\n) if /"method":"initialize"/;
                if (/"method":"session\/new"/) {
                    my $padding = 'x' x $ARGV[0];
                    print qq({"jsonrpc":"2.0","id":$id,"result":{"sessionId":"private","configOptions":[{"id":"raw","padding":"$padding"}]}}\n);
                }
                die "unexpected prompt or close" if /"method":"session\/(prompt|close)"/;
            }
            """;
        _boxLauncher.Launch = _ => Task.FromResult<IAgentLink>(adapter = AgentConnection.Start(
            new AgentCommand("/usr/bin/perl", ["-e", script, length.ToString(System.Globalization.CultureInfo.InvariantCulture)], "/tmp"),
            new UnclaimedPermissions(), TranscriptLog.Discard()));
        var target = (await GetJsonAsync(_desktop!, DesktopToken, $"/agents?machine={Box}"))
            .GetProperty("agents")[0].GetProperty("targetId").GetString()!;
        using var socket = await OpenSocketAsync(_desktop!);
        await DiscoverOverSocketAsync(socket, target);
        var text = await SocketReadAsync(socket);
        var frame = JsonDocument.Parse(text).RootElement;
        if (oversized)
        {
            Assert.Equal("error", frame.GetProperty("t").GetString());
            Assert.Equal("failed", frame.GetProperty("code").GetString());
            Assert.Equal("the answer is over 4 MB", frame.GetProperty("message").GetString());
            Assert.False(frame.TryGetProperty("result", out _));
        }
        else
        {
            Assert.Equal("result", frame.GetProperty("t").GetString());
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(text), limit - 4096, limit);
            var padding = frame.GetProperty("result").GetProperty("options")[0].GetProperty("padding").GetString();
            Assert.Equal(new string('x', length), padding);
        }
        await adapter!.Exited.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(adapter.ProcessId));
        Assert.Equal(WebSocketState.Open, socket.State);
        await AssertNoDiscoveryRowsAsync();
    }

    [Fact]
    public async Task Composer_transport_socket_cancel_reaches_the_pinned_owner_and_disposes_exact_held_ACP()
    {
        var target = (await GetJsonAsync(_desktop!, DesktopToken, $"/agents?machine={Box}"))
            .GetProperty("agents")[0].GetProperty("targetId").GetString()!;
        using var socket = await OpenSocketAsync(_desktop!);
        await DiscoverOverSocketAsync(socket, target);
        var held = await _boxLauncher.NextAsync();
        await held.ReadAsync("initialize"); // The pinned owning socket handler is inside ACP with no response available.
        await SendTextAsync(socket, """{"t":"cancel","id":1}""");
        var frame = JsonDocument.Parse(await SocketReadAsync(socket)).RootElement;
        Assert.Equal("cancelled", frame.GetProperty("code").GetString());
        await held.Exited.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(held.Disposed);
        Assert.Equal(1, _boxLauncher.Launches);
        await AssertNoDiscoveryRowsAsync();
    }

    [Fact]
    public async Task Composer_transport_held_result_refuses_MachineSet_replacement_without_touching_replacement()
    {
        using var file = new TempDatabase();
        using var db = TestDatabase.Open(file.Path);
        var topics = new Topics();
        using var hub = new LiveHub(RowStore.Open(db));
        var store = SessionStore.Open(db, TimeProvider.System, "/tmp");
        var status = new MachineStatus(topics, [Box]);
        await using var captured = new RemoteMachine(Box, _box.Address, BoxToken, hub, store, _log, status);
        var replacement = new FakeMachine(Box, store);
        var machines = new MachineSet(new FakeMachine(null, store), captured);
        var calls = new SocketDispatcher(db.Writer, topics, TimeProvider.System, NullLogger.Instance);
        new ConfigFeature(store, machines).Register(calls);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        var endpoint = new SocketEndpoint(DesktopToken, calls, "test", TimeProvider.System);
        app.Map("/socket", endpoint.HandleAsync);
        app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
        await app.StartAsync();
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{new Uri(app.Urls.Single()).Authority}/socket"), CancellationToken.None);
        await SendTextAsync(socket, JsonSerializer.Serialize(new { t = "hello", token = DesktopToken, protocol = 1 }));
        Assert.Contains("welcome", await SocketReadAsync(socket));
        await captured.ProbeAsync(default);
        await captured.ReadMirrorOwnerAsync(default);
        var target = (await captured.AgentsAsync(CancellationToken.None))[0].TargetId!;
        await DiscoverOverSocketAsync(socket, target);
        var held = await _boxLauncher.NextAsync();
        var initialize = await held.ReadAsync("initialize");
        machines.Set(replacement); // Captured remote is already awaited; only its answer can unblock the call.
        await held.ReplyAsync(initialize, """{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[]}""");
        await held.OpenAsync(configOptions: """[{"id":"from-old-machine"}]""");
        var frame = JsonDocument.Parse(await SocketReadAsync(socket)).RootElement;
        Assert.Equal("error", frame.GetProperty("t").GetString());
        Assert.Equal("failed", frame.GetProperty("code").GetString());
        Assert.Equal("The selected machine changed.", frame.GetProperty("message").GetString());
        Assert.Same(replacement, machines.Named(Box));
        Assert.True(held.Disposed);
        Assert.Equal(1, _boxLauncher.Launches);
        Assert.Empty(store.List(null, 10).Sessions);
        Assert.Empty(replacement.Asked);
        await AssertNoDiscoveryRowsAsync();
        socket.Abort();
        await app.StopAsync();
    }

    private static Task DiscoverOverSocketAsync(WebSocket socket, string target) => SendTextAsync(socket,
        JsonSerializer.Serialize(new { t = "call", id = 1, feature = "config", method = "discover", args = new { agent = "claude", targetId = target, cwd = "/tmp", machine = Box } }));

    private async Task AssertNoDiscoveryRowsAsync()
    {
        Assert.Equal(0, _desktopLauncher.Launches);
        Assert.Empty((await GetJsonAsync(_box, BoxToken, "/sessions")).GetProperty("sessions").EnumerateArray());
        Assert.Empty((await GetJsonAsync(_desktop!, DesktopToken, "/sessions")).GetProperty("sessions").EnumerateArray());
        using var owner = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_boxHome, "assembly.db")};Mode=ReadOnly;Pooling=False");
        owner.Open();
        using var command = owner.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM rows";
        Assert.Equal(0L, command.ExecuteScalar());
    }
}
