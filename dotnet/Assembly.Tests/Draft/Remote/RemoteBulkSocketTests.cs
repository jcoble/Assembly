using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Terminal;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Draft.Terminal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Remote;

public sealed class RemoteBulkSocketTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly TempDatabase _ownerFile = new(), _desktopFile = new();
    private readonly List<SocketEndpoint> _endpoints = [];
    private readonly Gate _gate = new();
    private AppDatabase _ownerDb = null!, _desktopDb = null!;
    private LiveHub _ownerHub = null!, _desktopHub = null!;
    private RowStore _ownerRows = null!;
    private SessionStore _desktopSessions = null!;
    private TerminalHost _terminals = null!;
    private RemoteMachine _machine = null!;
    private WebApplication _ownerApp = null!, _desktopApp = null!;
    private Uri _ownerAddress = null!, _desktopAddress = null!;
    private int _legacyToolHits;

    public async Task InitializeAsync()
    {
        var ownerTopics = new TopicSet();
        var desktopTopics = new TopicSet();
        _ownerDb = AppDatabase.Open(_ownerFile.Path, TimeProvider.System, ownerTopics);
        _desktopDb = AppDatabase.Open(_desktopFile.Path, TimeProvider.System, desktopTopics);
        _ownerRows = RowStore.Open(_ownerDb);
        _ownerHub = new LiveHub(_ownerRows, topics: ownerTopics);
        _desktopHub = new LiveHub(RowStore.Open(_desktopDb), topics: desktopTopics);
        var ownerSessions = SessionStore.Open(_ownerDb, TimeProvider.System, "checkouts");
        ownerSessions.Add("s", "claude", "repo", null!);
        _desktopSessions = SessionStore.Open(_desktopDb, TimeProvider.System, "checkouts");
        _desktopSessions.Add("s", "claude", "repo", "box");
        var ownerMachines = new MachineSet(new FakeMachine(null, ownerSessions));
        var ownerCalls = new SocketDispatcher(_ownerDb.Writer, ownerTopics, TimeProvider.System, NullLogger.Instance);
        new RowsFeature(_ownerHub, _ownerRows, ownerSessions, ownerMachines).Register(new GatedRegistry(ownerCalls, _gate));
        new SessionsFeature(ownerSessions, ownerMachines).Register(ownerCalls);
        _terminals = new TerminalHost(new FakePtyStarter(), ownerTopics, TimeProvider.System);
        new TerminalFeature(_terminals).Register(ownerCalls);
        _ownerApp = CreateApp();
        Map(_ownerApp, ownerCalls, "/socket");
        Map(_ownerApp, ownerCalls, "/socket/bulk", bulk: true);
        Map(_ownerApp, ownerCalls, "/socket/terminal", terminal: true);
        _ownerApp.Map("/sessions/{**path}", context =>
        {
            Interlocked.Increment(ref _legacyToolHits);
            context.Response.StatusCode = 410;
            return Task.CompletedTask;
        });
        await _ownerApp.StartAsync();
        _ownerAddress = new Uri(_ownerApp.Urls.Single());
        _machine = new RemoteMachine("box", _ownerAddress, "token", _desktopHub, _desktopSessions, TextWriter.Null,
            new MachineStatus(desktopTopics, ["box"]));
        var desktopMachines = new MachineSet(new FakeMachine(null, _desktopSessions), _machine);
        var desktopCalls = new SocketDispatcher(_desktopDb.Writer, desktopTopics, TimeProvider.System, NullLogger.Instance);
        new RowsFeature(_desktopHub, RowStore.Open(_desktopDb), _desktopSessions, desktopMachines).Register(desktopCalls);
        _desktopApp = CreateApp();
        Map(_desktopApp, desktopCalls, "/socket/bulk", bulk: true);
        await _desktopApp.StartAsync();
        _desktopAddress = new Uri(_desktopApp.Urls.Single());
        await _machine.ProbeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Remote_tool_detail_keeps_one_cap_and_exact_totals()
    {
        var output = new string('a', 5 * RowsFeature.ToolCap) + "€";
        var diff = new string('b', RowsFeature.ToolCap - 1) + "😀" + new string('c', 4 * RowsFeature.ToolCap);
        Seed(output, diff);
        using var socket = await OpenAsync(_desktopAddress, "/socket/bulk");
        await ToolAsync(socket, 1);
        var detail = (await ReadAsync(socket)).GetProperty("result");
        Assert.Equal(Encoding.UTF8.GetByteCount(output), detail.GetProperty("outputBytes").GetInt64());
        Assert.Equal(Encoding.UTF8.GetByteCount(diff), detail.GetProperty("diffBytes").GetInt64());
        Assert.True(detail.GetProperty("truncated").GetBoolean());
        Assert.Equal(output[..RowsFeature.ToolCap], detail.GetProperty("output").GetString());
        Assert.Equal(diff[..(RowsFeature.ToolCap - 1)], detail.GetProperty("diff").GetString());
        Assert.Equal("input", detail.GetProperty("input").GetString());
        Assert.Equal("path", detail.GetProperty("path").GetString());
        Assert.Equal(0, _legacyToolHits);
        Assert.Empty(RowStore.Open(_desktopDb).RowsAfter("s", 0));
        Assert.Null(await _machine.ToolCallAsync(_desktopSessions.Get("s")!, "missing", CancellationToken.None));
        await ToolAsync(socket, 2, "missing");
        Assert.Equal("unknown tool call", (await ReadAsync(socket)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Raw_escaped_result_over_frame_fails_without_shortened_success()
    {
        var text = new string('\u0001', RowsFeature.ToolCap);
        Seed(text, text);
        var raw = new StoredToolCall("summary", "input", text, text, "path", text.Length, text.Length);
        Assert.True(SocketDispatcher.Result(1, JsonSerializer.SerializeToElement(raw, RowJsonContext.Rows.StoredToolCall)).Length > SocketConnection.MaxFrame);
        using var socket = await OpenAsync(_desktopAddress, "/socket/bulk");
        await ToolAsync(socket, 1);
        AssertOversize(await ReadAsync(socket));
        Assert.Equal(0, _legacyToolHits);
        var args = JsonSerializer.SerializeToElement(new RowsFeature.SessionArgs("s"), RowJsonContext.Rows.SessionArgs);
        Assert.Equal(JsonValueKind.Array,
            (await _machine.CallAsync("rows", "files", args, false, CancellationToken.None).WaitAsync(Wait)).ValueKind);
    }

    [Fact]
    public async Task Final_escaped_display_over_frame_fails_without_shortened_success()
    {
        // The public result adds truncated. Find the exact boundary using the real serializers,
        // including their envelope and the optional-field omission rules.
        const string output = "\u0001";
        var raw = new StoredToolCall("summary", "", output, null, "path", 1, 0);
        var fixedBytes = SocketDispatcher.Result(1, JsonSerializer.SerializeToElement(raw, RowJsonContext.Rows.StoredToolCall)).Length;
        var input = new string('x', SocketConnection.MaxFrame - fixedBytes);
        raw = raw with { Input = input };
        var display = new RowsFeature.ToolDetail("summary", input, output, null, "path", false, 1, 0);
        Assert.Equal(SocketConnection.MaxFrame, SocketDispatcher.Result(1, JsonSerializer.SerializeToElement(raw, RowJsonContext.Rows.StoredToolCall)).Length);
        Assert.True(SocketDispatcher.Result(1, JsonSerializer.SerializeToElement(display, RowJsonContext.Rows.ToolDetail)).Length > SocketConnection.MaxFrame);
        Seed(output, null, input);
        // Prove the owner sends the complete raw value successfully before the final boundary fails.
        using var owner = await OpenAsync(_ownerAddress, "/socket/bulk");
        await SendAsync(owner, """{"t":"call","id":1,"feature":"rows","method":"toolRaw","args":{"session":"s","item":"t","outputMaxBytes":1048576,"diffMaxBytes":1048576}}""");
        Assert.Equal(input, (await ReadAsync(owner)).GetProperty("result").GetProperty("input").GetString());
        using var socket = await OpenAsync(_desktopAddress, "/socket/bulk");
        await ToolAsync(socket, 1);
        AssertOversize(await ReadAsync(socket));
        Assert.Equal(0, _legacyToolHits);
    }

    [Fact]
    public async Task Cancelling_remote_detail_cancels_owner_and_releases_bulk()
    {
        Seed("output", null);
        _gate.Hold = true;
        _gate.HoldCleanup = true;
        using var cancellation = new CancellationTokenSource();
        var pending = _machine.ToolCallAsync(_desktopSessions.Get("s")!, "t", cancellation.Token);
        await _gate.Entered.Task.WaitAsync(Wait);
        cancellation.Cancel();
        await _gate.CancellationObserved.Task.WaitAsync(Wait);
        Assert.False(pending.IsCompleted);
        Assert.False(_gate.CancelledByClose);
        Assert.False(string.IsNullOrEmpty(_gate.Connection));
        _gate.CleanupRelease.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Wait));
        await _gate.Settled.Task.WaitAsync(Wait);
        Assert.True(_gate.Cancelled);
        _gate.Hold = false;
        using var socket = await OpenAsync(_desktopAddress, "/socket/bulk");
        await ToolAsync(socket, 1);
        Assert.Equal("output", (await ReadAsync(socket)).GetProperty("result").GetProperty("output").GetString());
        Assert.Equal(0, _legacyToolHits);
    }

    [Fact]
    public async Task Bulk_wait_does_not_hold_control_or_terminal()
    {
        Seed("output", null);
        _gate.Hold = true;
        using var socket = await OpenAsync(_desktopAddress, "/socket/bulk");
        await ToolAsync(socket, 1);
        await _gate.Entered.Task.WaitAsync(Wait);
        var args = JsonSerializer.SerializeToElement(new RowsFeature.SessionArgs("s"), RowJsonContext.Rows.SessionArgs);
        var control = await _machine.CallAsync("rows", "files", args, false, CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(JsonValueKind.Array, control.ValueKind);
        using var terminal = await OpenAsync(_ownerAddress, "/socket/terminal");
        await SendAsync(terminal, """{"t":"call","id":1,"feature":"terminal","method":"list","args":{}}""");
        Assert.Equal(JsonValueKind.Array, (await ReadAsync(terminal)).GetProperty("result").ValueKind);
        Assert.False(_gate.Settled.Task.IsCompleted);
        _gate.Release.TrySetResult();
        _gate.CleanupRelease.TrySetResult();
        Assert.Equal("output", (await ReadAsync(socket)).GetProperty("result").GetProperty("output").GetString());
        await _gate.Settled.Task.WaitAsync(Wait);
    }

    private void Seed(string? output, string? diff, string input = "input")
    {
        var batch = new RowBatch();
        batch.Rows.Add(new NewRow("run", "t", RowKind.Tool, JsonDocument.Parse("""{"summary":"summary"}""").RootElement.Clone(), 1));
        batch.Details.Add(new ToolDetail("t", Input: input, Output: output, Diff: diff, Path: "path"));
        _ownerRows.Commit("s", batch);
    }

    private static void AssertOversize(JsonElement answer)
    {
        Assert.Equal("failed", answer.GetProperty("code").GetString());
        Assert.Equal("the answer is over 4 MB", answer.GetProperty("message").GetString());
        Assert.False(answer.TryGetProperty("result", out _));
    }

    private static WebApplication CreateApp()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.UseWebSockets();
        return app;
    }

    private void Map(WebApplication app, SocketDispatcher calls, string path, bool bulk = false, bool terminal = false)
    {
        var endpoint = new SocketEndpoint("token", calls, "box", TimeProvider.System, bulk, terminal);
        _endpoints.Add(endpoint);
        app.Map(path, endpoint.HandleAsync);
    }

    private static async Task<ClientWebSocket> OpenAsync(Uri address, string path)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{address.Authority}{path}"), CancellationToken.None);
        await SendAsync(socket, """{"t":"hello","protocol":1,"token":"token"}""");
        Assert.Equal("welcome", (await ReadAsync(socket)).GetProperty("t").GetString());
        return socket;
    }

    private static Task ToolAsync(WebSocket socket, long id, string item = "t") =>
        SendAsync(socket, $$$"""{"t":"call","id":{{{id}}},"feature":"rows","method":"tool","args":{"session":"s","item":"{{{item}}}"}}""");

    private static Task SendAsync(WebSocket socket, string frame) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> ReadAsync(WebSocket socket)
    {
        using var deadline = new CancellationTokenSource(Wait);
        using var frame = new MemoryStream();
        var chunk = new byte[16384];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(chunk, deadline.Token);
            Assert.NotEqual(WebSocketMessageType.Close, received.MessageType);
            frame.Write(chunk, 0, received.Count);
        } while (!received.EndOfMessage);
        using var document = JsonDocument.Parse(frame.ToArray());
        return document.RootElement.Clone();
    }

    public async Task DisposeAsync()
    {
        _gate.Release.TrySetResult();
        _gate.CleanupRelease.TrySetResult();
        await _machine.DisposeAsync();
        foreach (var endpoint in _endpoints) endpoint.CloseAll();
        await _desktopApp.DisposeAsync();
        await _ownerApp.DisposeAsync();
        await _terminals.DisposeAsync();
        _desktopHub.Dispose();
        _ownerHub.Dispose();
        _desktopDb.Dispose();
        _ownerDb.Dispose();
        _desktopFile.Dispose();
        _ownerFile.Dispose();
    }

    private sealed class Gate
    {
        internal bool Hold, HoldCleanup, Cancelled, CancelledByClose;
        internal string? Connection;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CleanupRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Keep the real owner query and serializer; only delay its execution to observe cancellation
    // and traffic on the other two endpoint kinds deterministically.
    private sealed class GatedRegistry(ICallRegistry inner, Gate gate) : ICallRegistry
    {
        public void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
            Func<TArgs, CallContext, Task<TResult>> handler, bool bulk = false) =>
            inner.Read(feature, method, args, result, async (value, call) =>
            {
                if (feature != "rows" || method != "toolRaw" || !gate.Hold) return await handler(value, call);
                gate.Connection = call.Connection;
                gate.Entered.TrySetResult();
                try { await gate.Release.Task.WaitAsync(call.Cancellation); return await handler(value, call); }
                catch (OperationCanceledException)
                {
                    gate.Cancelled = true;
                    gate.CancelledByClose = call.Closed.IsCancellationRequested;
                    gate.CancellationObserved.TrySetResult();
                    if (gate.HoldCleanup) await gate.CleanupRelease.Task;
                    throw;
                }
                finally { gate.Settled.TrySetResult(); }
            }, bulk);
        public void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
            Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler, Func<TArgs, string?>? order = null,
            Action<TArgs, CallContext>? failed = null, bool bulk = false) => inner.Write(feature, method, args, result, handler, order, failed, bulk);
        public void Save<TValue>(string feature, JsonTypeInfo<TValue> value, Action<string, TValue, IWriteScope> save) => inner.Save(feature, value, save);
        public void Closed(Action<string> connection) => inner.Closed(connection);
        public void Topic<TItem>(string topic, JsonTypeInfo<TItem> item, Func<TItem, string> key,
            Func<string?, CallContext, IReadOnlyList<TItem>> state, bool terminal = false) => inner.Topic(topic, item, key, state, terminal);
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state,
            Action<string, CallContext>? opened = null) => inner.Rows(topic, state, opened);
    }
}
