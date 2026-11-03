using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Terminal;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Socket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed class WelcomeSocketTests
{
    [Theory]
    [InlineData("/socket")]
    [InlineData("/socket/bulk")]
    [InlineData("/socket/terminal")]
    public async Task Authenticated_endpoints_advertise_the_same_build_format_and_optional_engine(string path)
    {
        await using var host = await Host.Start();
        using var socket = new ClientWebSocket();
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(host.Address(path), bound.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"t":"hello","protocol":1,"token":"token"}"""), WebSocketMessageType.Text, true, bound.Token);
        var bytes = new byte[4096]; var received = await socket.ReceiveAsync(bytes.AsMemory(), bound.Token);
        Assert.Equal(WebSocketMessageType.Text, received.MessageType); Assert.True(received.EndOfMessage);
        using var json = JsonDocument.Parse(bytes.AsMemory(0, received.Count)); var welcome = json.RootElement;
        Assert.Equal("welcome", welcome.GetProperty("t").GetString());
        Assert.Equal(1, welcome.GetProperty("protocol").GetInt32());
        Assert.Equal("owner-host", welcome.GetProperty("machine").GetString());
        Assert.Equal(RowStore.FormatVersion, welcome.GetProperty("format").GetInt32());
        Assert.Equal(BackendBuild.Commit, welcome.GetProperty("commit").GetString());
        Assert.Equal("actual-engine-id", welcome.GetProperty("engine").GetString());
    }

    [Theory]
    [InlineData("wrong-token", 1, 4401)]
    [InlineData("token", 2, 4400)]
    public async Task Authentication_or_protocol_refusal_sends_no_welcome(string token, int protocol, int closeCode)
    {
        await using var host = await Host.Start(); using var socket = new ClientWebSocket();
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(host.Address("/socket"), bound.Token);
        var hello = JsonSerializer.SerializeToUtf8Bytes(new { t = "hello", token, protocol });
        await socket.SendAsync(hello, WebSocketMessageType.Text, true, bound.Token);
        var received = await socket.ReceiveAsync(new byte[4096].AsMemory(), bound.Token);
        Assert.Equal(WebSocketMessageType.Close, received.MessageType); Assert.Equal(closeCode, (int)socket.CloseStatus!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.0.0")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0+one+two")]
    public void An_unstamped_build_is_explicit_json_null_and_has_no_fabricated_identity(string? version)
    {
        var commit = BackendBuild.ReadCommit(version); Assert.Null(commit);
        using var json = JsonDocument.Parse(SocketDispatcher.Frame(new WelcomeFrame(1, "owner", RowStore.FormatVersion, commit), SocketJson.Default.WelcomeFrame));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("commit").ValueKind);
        Assert.False(json.RootElement.TryGetProperty("engine", out _));
    }

    [Fact]
    public void Stamped_metadata_is_reported_verbatim_without_a_compatibility_policy()
    {
        Assert.Equal("actual-sdk-stamp", BackendBuild.ReadCommit("1.0.0+actual-sdk-stamp"));
    }

    [Theory]
    [InlineData("missing_format")]
    [InlineData("wrong_format")]
    [InlineData("missing_protocol")]
    [InlineData("wrong_protocol")]
    public async Task Terminal_refuses_incompatible_welcome_before_any_call_or_subscription(string failure)
    {
        var welcome = new Dictionary<string, object?>
        {
            ["t"] = "welcome",
            ["protocol"] = 1,
            ["machine"] = "owner-host",
            ["format"] = RowStore.FormatVersion,
            ["commit"] = "different-build"
        };
        if (failure == "missing_format") welcome.Remove("format");
        if (failure == "wrong_format") welcome["format"] = RowStore.FormatVersion + 1;
        if (failure == "missing_protocol") welcome.Remove("protocol");
        if (failure == "wrong_protocol") welcome["protocol"] = 2;
        await using var host = await Host.Start(JsonSerializer.Serialize(welcome));
        await using var terminal = new RemoteTerminalSocket("test-box", host.Address("/"), "token", new global::Assembly.Infrastructure.Topics.Topics());
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<CallFailed>(() => terminal.CallAsync("list", new ListArgs(),
            TerminalJson.Default.ListArgs, TerminalJson.Default.IReadOnlyListTerminalInfo, bound.Token));
        Assert.Contains("test-box", error.Message);
        if (failure.EndsWith("format", StringComparison.Ordinal))
        {
            var actual = failure == "missing_format" ? 0 : RowStore.FormatVersion + 1;
            Assert.Contains($"stores rows in format {actual}; this computer reads format {RowStore.FormatVersion}", error.Message);
        }
        else Assert.Contains("this computer speaks protocol 1", error.Message);
        Assert.Equal(0, await host.AfterWelcome.Task.WaitAsync(bound.Token));
    }

    [Fact]
    public async Task A_different_commit_does_not_prevent_a_compatible_terminal_call()
    {
        await using var host = await Host.Start($$"""{"t":"welcome","protocol":1,"machine":"owner-host","format":{{RowStore.FormatVersion}},"commit":"different-build"}""");
        await using var terminal = new RemoteTerminalSocket("test-box", host.Address("/"), "token", new global::Assembly.Infrastructure.Topics.Topics());
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await terminal.CallAsync("list", new ListArgs(), TerminalJson.Default.ListArgs,
            TerminalJson.Default.IReadOnlyListTerminalInfo, bound.Token);
        Assert.Empty(result); Assert.Equal(1, await host.AfterWelcome.Task.WaitAsync(bound.Token));
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly TempDatabase _file = new();
        private AppDatabase _db = null!;
        private WebApplication _app = null!;
        private readonly List<SocketEndpoint> _endpoints = [];
        private Uri _address = null!;
        internal TaskCompletionSource<int> AfterWelcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Uri Address(string path) => new UriBuilder(_address) { Scheme = "ws", Path = path }.Uri;
        internal static async Task<Host> Start(string? scriptedWelcome = null)
        {
            var host = new Host();
            host._db = TestDatabase.Open(host._file.Path);
            var calls = new SocketDispatcher(host._db.Writer, new(), TimeProvider.System, NullLogger.Instance);
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            host._app = builder.Build(); host._app.UseWebSockets();
            foreach (var path in new[] { "/socket", "/socket/bulk", "/socket/terminal" })
            {
                var endpoint = new SocketEndpoint("token", calls, "owner-host", TimeProvider.System,
                    bulk: path == "/socket/bulk", terminal: path == "/socket/terminal", engineMachine: "actual-engine-id");
                host._endpoints.Add(endpoint);
                if (scriptedWelcome is not null && path == "/socket/terminal")
                    host._app.Map(path, async context =>
                    {
                        using var socket = await context.WebSockets.AcceptWebSocketAsync();
                        var bytes = new byte[4096];
                        try
                        {
                            await socket.ReceiveAsync(bytes.AsMemory(), context.RequestAborted);
                            await socket.SendAsync(Encoding.UTF8.GetBytes(scriptedWelcome), WebSocketMessageType.Text, true, context.RequestAborted);
                            var frame = await socket.ReceiveAsync(bytes.AsMemory(), context.RequestAborted);
                            if (frame.MessageType != WebSocketMessageType.Text) { host.AfterWelcome.TrySetResult(0); return; }
                            host.AfterWelcome.TrySetResult(1);
                            using var call = JsonDocument.Parse(bytes.AsMemory(0, frame.Count));
                            var response = Encoding.UTF8.GetBytes($$"""{"t":"result","id":{{call.RootElement.GetProperty("id").GetInt64()}},"result":[]}""");
                            await socket.SendAsync(response, WebSocketMessageType.Text, true, context.RequestAborted);
                            await socket.ReceiveAsync(bytes.AsMemory(), context.RequestAborted);
                        }
                        catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
                        finally { host.AfterWelcome.TrySetResult(0); }
                    });
                else host._app.Map(path, endpoint.HandleAsync);
            }
            await host._app.StartAsync();
            host._address = new(host._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            return host;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var endpoint in _endpoints) endpoint.CloseAll();
            await _app.DisposeAsync(); _db.Dispose(); _file.Dispose();
        }
    }
}
