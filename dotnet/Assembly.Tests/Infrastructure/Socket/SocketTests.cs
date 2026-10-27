using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Infrastructure.Socket;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Socket;

/// <summary>The socket (plan 5.1 to 5.3, B4) over a real loopback socket, with calls registered by each test.</summary>
public sealed partial class SocketTests : IAsyncLifetime
{
    private const string Token = "socket-token";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new();
    private readonly FakeWriter _writer = new();
    private readonly TopicSet _topics = new();
    private readonly FakeLog _log = new();
    private SocketDispatcher _calls = null!;
    private SocketEndpoint _endpoint = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;

    public async Task InitializeAsync()
    {
        _calls = new SocketDispatcher(_writer, _topics, _time, _log);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        _endpoint = new SocketEndpoint(Token, _calls, "test-machine", _time);
        _app.Map("/socket", _endpoint.HandleAsync);
        // As ApiServer does.
        _app.Lifetime.ApplicationStopping.Register(_endpoint.CloseAll);
        await _app.StartAsync();
        var http = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _address = new Uri($"ws://{http.Authority}/socket");
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        foreach (var disposable in _disposeAfter)
            disposable.Dispose();
    }

    [Fact]
    public async Task A_call_answers_its_result_and_an_unknown_one_not_found()
    {
        _calls.Read("notes", "echo", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult(note with { Text = note.Text + "!" }));
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "notes", "echo", """{"text":"hi"}""");
        await CallAsync(socket, 2, "notes", "missing", "{}");

        var answers = await ReadByIdAsync(socket, 2);
        Assert.Equal("""{"t":"result","id":1,"result":{"text":"hi!"}}""", answers[0]);
        Assert.Equal("not_found", Code(answers[1], id: 2));
    }

    [Fact]
    public async Task Bad_args_answer_bad_args()
    {
        _calls.Read("notes", "echo", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult(note));
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "notes", "echo", """{"text":5}""");
        await SendAsync(socket, """{"t":"call","id":2,"feature":"notes","method":"echo"}""");

        Assert.Equal("bad_args", Code(await ReadAsync(socket), id: 1));
        Assert.Equal("bad_args", Code(await ReadAsync(socket), id: 2));
    }

    [Theory]
    [InlineData("""{"t":"call","id":1,"feature":"notes","method":"echo","args":{}}""", 4400)]
    [InlineData("""{"t":"hello","token":"socket-token","protocol":2}""", 4400)]
    [InlineData("not json", 4400)]
    [InlineData("""{"t":"hello","token":"wrong-token","protocol":1}""", 4401)]
    public async Task A_socket_that_starts_without_a_valid_hello_is_closed_before_any_data(string first, int code)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(_address, CancellationToken.None);

        await SendAsync(socket, first);

        var buffer = new byte[1024];
        var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(WebSocketMessageType.Close, received.MessageType);
        Assert.Equal(code, (int)socket.CloseStatus!);
    }

    [Fact]
    public async Task The_welcome_names_the_protocol_and_the_machine()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(_address, CancellationToken.None);

        await SendAsync(socket, """{"t":"hello","token":"socket-token","protocol":1}""");

        Assert.Equal("""{"t":"welcome","protocol":1,"machine":"test-machine"}""", await ReadAsync(socket));
    }

    // Pins: base#10
    [Fact]
    public async Task Cancel_fires_the_handlers_token()
    {
        var running = new Running();
        _calls.Read("notes", "wait", TestJson.Default.Note, TestJson.Default.Note, running.WaitAsync);
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "notes", "wait", "{}");
        await running.Started.Task.WaitAsync(Wait);
        await SendAsync(socket, """{"t":"cancel","id":1}""");

        Assert.Equal("cancelled", Code(await ReadAsync(socket), id: 1));
        Assert.True(running.Cancelled.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Closing_the_socket_cancels_running_reads_and_queued_writes_still_commit()
    {
        var read = new Running();
        _calls.Read("notes", "wait", TestJson.Default.Note, TestJson.Default.Note, read.WaitAsync);
        var gate = new TaskCompletionSource();
        var first = new TaskCompletionSource();
        _calls.Write("notes", "first", TestJson.Default.Note, TestJson.Default.Note, async (note, _) =>
        {
            first.SetResult();
            await gate.Task;
            return _ => note;
        });
        _calls.Write("notes", "second", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult<Func<IWriteScope, Note>>(_ => note));
        _calls.Read("notes", "echo", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult(note));
        var socket = await OpenAsync();

        await CallAsync(socket, 1, "notes", "wait", "{}");
        await CallAsync(socket, 2, "notes", "first", "{}");
        await CallAsync(socket, 3, "notes", "second", "{}");
        // Frames are read in order, so once this answers, the second write is queued.
        await CallAsync(socket, 4, "notes", "echo", "{}");
        Assert.Equal("""{"t":"result","id":4,"result":{"text":""}}""", await ReadAsync(socket));
        await read.Started.Task.WaitAsync(Wait);
        await first.Task.WaitAsync(Wait);
        socket.Abort();
        socket.Dispose();

        await read.Cancelled.Task.WaitAsync(Wait);
        Assert.Empty(_writer.Written);
        gate.SetResult();
        await _writer.Count(2).WaitAsync(Wait);
        Assert.Equal(["notes/first", "notes/second"], _writer.Written);
    }

    [Fact]
    public async Task Two_calls_at_once_each_get_their_own_answer()
    {
        var gates = new Dictionary<string, TaskCompletionSource>
        { ["a"] = new(TaskCreationOptions.RunContinuationsAsynchronously), ["b"] = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        _calls.Read("notes", "gated", TestJson.Default.Note, TestJson.Default.Note, async (note, _) =>
        {
            await gates[note.Text].Task;
            return note;
        });
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "notes", "gated", """{"text":"a"}""");
        await CallAsync(socket, 2, "notes", "gated", """{"text":"b"}""");
        gates["b"].SetResult();
        var second = await ReadAsync(socket);
        gates["a"].SetResult();
        var firstAnswer = await ReadAsync(socket);

        Assert.Equal("""{"t":"result","id":2,"result":{"text":"b"}}""", second);
        Assert.Equal("""{"t":"result","id":1,"result":{"text":"a"}}""", firstAnswer);
    }

    [Fact]
    public async Task A_read_that_blocks_doesnt_hold_up_the_socket()
    {
        using var gate = new ManualResetEventSlim();
        _calls.Read("notes", "blocking", TestJson.Default.Note, TestJson.Default.Note, (note, _) =>
        {
            gate.Wait(Wait);
            return Task.FromResult(note);
        });
        _calls.Read("notes", "echo", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult(note));
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "notes", "blocking", "{}");
        await CallAsync(socket, 2, "notes", "echo", "{}");
        var echo = await ReadAsync(socket);
        gate.Set();

        Assert.Equal("""{"t":"result","id":2,"result":{"text":""}}""", echo);
        Assert.Equal("""{"t":"result","id":1,"result":{"text":""}}""", await ReadAsync(socket));
    }

    // The cancel is answered while the write ahead of it still runs, not once it ends.
    [Fact]
    public async Task A_write_cancelled_while_queued_answers_cancelled_at_once_and_never_runs()
    {
        var gate = new TaskCompletionSource();
        var first = new TaskCompletionSource();
        _calls.Write("notes", "first", TestJson.Default.Note, TestJson.Default.Note, async (note, _) =>
        {
            first.SetResult();
            await gate.Task;
            return _ => note;
        });
        _calls.Write("notes", "second", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult<Func<IWriteScope, Note>>(_ => note));
        _calls.Read("notes", "echo", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult(note));
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "notes", "first", "{}");
        await first.Task.WaitAsync(Wait);
        await CallAsync(socket, 2, "notes", "second", "{}");
        await SendAsync(socket, """{"t":"cancel","id":2}""");
        await CallAsync(socket, 3, "notes", "echo", "{}");
        string[] answers;
        try
        {
            answers = await ReadByIdAsync(socket, 2);
        }
        finally
        {
            gate.SetResult();
        }

        Assert.Equal("cancelled", Code(answers[0], id: 2));
        Assert.Equal("""{"t":"result","id":3,"result":{"text":""}}""", answers[1]);
        Assert.Equal("""{"t":"result","id":1,"result":{"text":""}}""", await ReadAsync(socket));
        Assert.Equal(["notes/first"], _writer.Written);
    }

    // A save the page cancels once it's committing still answers result, so the page never takes a committed save for
    // a lost one.
    [Fact]
    public async Task A_cancel_that_comes_while_a_write_commits_answers_result()
    {
        using var release = new ManualResetEventSlim();
        var committing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls.Write("notes", "save", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult<Func<IWriteScope, Note>>(_ =>
        {
            committing.SetResult();
            release.Wait(Wait);
            return note;
        }));
        _calls.Read("notes", "echo", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult(note));
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "notes", "save", """{"text":"kept"}""");
        await committing.Task.WaitAsync(Wait);
        await SendAsync(socket, """{"t":"cancel","id":1}""");
        // Frames are read in order, so once this answers, the cancel has been seen.
        await CallAsync(socket, 2, "notes", "echo", "{}");
        Assert.Equal("""{"t":"result","id":2,"result":{"text":""}}""", await ReadAsync(socket));
        release.Set();

        Assert.Equal("""{"t":"result","id":1,"result":{"text":"kept"}}""", await ReadAsync(socket));
        Assert.Equal(["notes/save"], _writer.Written);
    }

    // A write out of time while queued never runs; one whose handler returns after the limit did its work, so it
    // answers its result.
    [Fact]
    public async Task A_write_out_of_time_in_the_queue_answers_timeout_and_one_that_ran_answers_its_result()
    {
        var gate = new TaskCompletionSource();
        var first = new TaskCompletionSource();
        _calls.Write("notes", "first", TestJson.Default.Note, TestJson.Default.Note, async (note, _) =>
        {
            first.SetResult();
            await gate.Task;
            return _ => note;
        });
        _calls.Write("notes", "second", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult<Func<IWriteScope, Note>>(_ => note));
        _calls.Read("notes", "echo", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult(note));
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "notes", "first", "{}");
        await first.Task.WaitAsync(Wait);
        await CallAsync(socket, 2, "notes", "second", "{}");
        // Frames are read in order, so once this answers, the second write is queued with its 30 s running.
        await CallAsync(socket, 3, "notes", "echo", "{}");
        Assert.Equal("""{"t":"result","id":3,"result":{"text":""}}""", await ReadAsync(socket));
        _time.Advance(SocketDispatcher.Limit);
        gate.SetResult();

        var answers = await ReadByIdAsync(socket, 2);
        Assert.Equal("""{"t":"result","id":1,"result":{"text":""}}""", answers[0]);
        Assert.Equal("timeout", Code(answers[1], id: 2));
        Assert.Equal(["notes/first"], _writer.Written);
    }

    [Fact]
    public async Task An_answer_over_4_MB_answers_failed()
    {
        _calls.Read("notes", "big", TestJson.Default.Note, TestJson.Default.Note, (_, _) => Task.FromResult(new Note(new string('x', 4 * 1024 * 1024))));
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "notes", "big", "{}");

        Assert.Equal("failed", Code(await ReadAsync(socket), id: 1));
    }

    [Fact]
    public async Task An_error_over_4_MB_answers_a_short_failed()
    {
        using var socket = await OpenAsync();

        // The call frame is exactly 4 MB, and its not_found message, with the name and a longer frame around it, is over.
        const string around = """{"t":"call","id":1,"feature":"","method":"missing","args":{}}""";
        await CallAsync(socket, 1, new string('x', SocketConnection.MaxFrame - around.Length), "missing", "{}");

        Assert.Equal("failed", Code(await ReadAsync(socket), id: 1));
    }

    [Fact]
    public async Task A_handler_that_throws_answers_failed_and_logs_at_error()
    {
        _calls.Read<Note, Note>("notes", "boom", TestJson.Default.Note, TestJson.Default.Note, (_, _) => throw new InvalidOperationException("broke"));
        _calls.Read<Note, Note>("notes", "refuse", TestJson.Default.Note, TestJson.Default.Note, (_, _) => throw new CallFailed("no such note"));
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "notes", "boom", "{}");
        await CallAsync(socket, 2, "notes", "refuse", "{}");

        var answers = await ReadByIdAsync(socket, 2);
        Assert.Equal("""{"t":"error","id":1,"code":"failed","message":"notes/boom failed"}""", answers[0]);
        Assert.Equal("""{"t":"error","id":2,"code":"failed","message":"no such note"}""", answers[1]);
        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("notes/boom"));
        Assert.DoesNotContain(_log.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("notes/refuse"));
    }

    [Fact]
    public async Task A_call_over_30_seconds_answers_timeout()
    {
        var running = new Running();
        _calls.Read("notes", "wait", TestJson.Default.Note, TestJson.Default.Note, running.WaitAsync);
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "notes", "wait", "{}");
        await running.Started.Task.WaitAsync(Wait);
        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(running.Cancelled.Task.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("timeout", Code(await ReadAsync(socket), id: 1));
    }

    [Fact]
    public async Task A_frame_over_4_MB_closes_the_socket()
    {
        using var socket = await OpenAsync();

        await SendAsync(socket, new string('x', 4 * 1024 * 1024 + 1));

        var received = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(WebSocketMessageType.Close, received.MessageType);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.CloseStatus);
    }

    private async Task<ClientWebSocket> OpenAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(_address, CancellationToken.None);
        await SendAsync(socket, """{"t":"hello","token":"socket-token","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadAsync(socket));
        return socket;
    }

    private static Task CallAsync(WebSocket socket, long id, string feature, string method, string args) =>
        SendAsync(socket, $$"""{"t":"call","id":{{id}},"feature":"{{feature}}","method":"{{method}}","args":{{args}}}""");

    private static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<string> ReadAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
        Assert.True(received.EndOfMessage);
        return Encoding.UTF8.GetString(buffer, 0, received.Count);
    }

    // The next answers, ordered by id: calls run at once, so their answers come in any order.
    private static async Task<string[]> ReadByIdAsync(WebSocket socket, int count)
    {
        var answers = new List<string>();
        for (var i = 0; i < count; i++)
            answers.Add(await ReadAsync(socket));
        return [.. answers.OrderBy(answer => JsonDocument.Parse(answer).RootElement.GetProperty("id").GetInt64())];
    }

    private static string Code(string frame, long id)
    {
        var root = JsonDocument.Parse(frame).RootElement;
        Assert.Equal("error", root.GetProperty("t").GetString());
        Assert.Equal(id, root.GetProperty("id").GetInt64());
        return root.GetProperty("code").GetString()!;
    }

    // A read that waits on its token until it's cancelled.
    private sealed class Running
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Note> WaitAsync(Note note, CallContext call)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, call.Cancellation);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
            return note;
        }
    }

    // Runs each database part as it comes and notes the write, in order: in a real writer once a test sets one.
    private sealed class FakeWriter : IAtomicWriter
    {
        public IAtomicWriter? Real { get; set; }

        private readonly List<string> _written = [];
        private readonly List<(int Count, TaskCompletionSource Done)> _waiting = [];

        public List<string> Written
        {
            get
            {
                lock (_written)
                    return [.. _written];
            }
        }

        public T Write<T>(string what, Func<IWriteScope, T> write)
        {
            lock (_written)
            {
                var result = Real is { } real ? real.Write(what, write) : write(null!);
                _written.Add(what);
                foreach (var (count, done) in _waiting)
                    if (_written.Count >= count)
                        done.TrySetResult();
                return result;
            }
        }

        public Task Count(int count)
        {
            lock (_written)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_written.Count >= count)
                    done.SetResult();
                _waiting.Add((count, done));
                return done.Task;
            }
        }
    }

    internal sealed class FakeLog : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public List<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                    return [.. _entries];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add((logLevel, $"{formatter(state, exception)} {exception}"));
        }
    }
}

public sealed record Note(string Text = "");

public sealed record Setting(string Key, string Value);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Note))]
[JsonSerializable(typeof(Setting))]
[JsonSerializable(typeof(string))]
internal sealed partial class TestJson : JsonSerializerContext;
