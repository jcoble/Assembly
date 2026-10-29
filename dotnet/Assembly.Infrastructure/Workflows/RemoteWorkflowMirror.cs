using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Workflows;
using Assembly.Application.Features.Workflows.Contracts;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Workflows;

/// <summary>One Board socket, one receive loop, and complete committed event replay. No pane owns this connection.</summary>
public sealed class RemoteWorkflowMirror(string name, Func<RemoteConfig?> current, WorkflowQueries queries, WorkflowTransitions transitions, Action unreachable) : IAsyncDisposable
{
    /// <summary>A mirror of one fixed address, for callers that hold the configuration themselves.</summary>
    public RemoteWorkflowMirror(RemoteConfig remote, WorkflowQueries queries, WorkflowTransitions transitions, Action unreachable)
        : this(remote.Name, () => remote, queries, transitions, unreachable) { }

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _send = new(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _calls = new();
    private Task _run = Task.CompletedTask;
    private TaskCompletionSource<ClientWebSocket>? _ready;
    private long _id;
    private bool _disposed;
    public string Name => name;

    /// <summary>A committed remote Board delivery names the session and run whose rows the desktop needs.</summary>
    public event Action<string, string>? AnswerDelivered;

    // Called under Topics' lock: queue only; the worker does the connection and all writes.
    public void Reconnect()
    {
        lock (_gate)
        {
            if (_disposed || !_run.IsCompleted) return;
            _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = _ready;
            _run = Task.Run(() => RunAsync(ready));
        }
    }

    private async Task RunAsync(TaskCompletionSource<ClientWebSocket> ready)
    {
        using var socket = new ClientWebSocket();
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        using var wake = new SemaphoreSlim(0, 1);
        var welcome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? receiver = null, catchup = null;
        void Wake() { if (wake.CurrentCount == 0) { try { wake.Release(); } catch (SemaphoreFullException) { } } }
        try
        {
            // Read at each connection, so the mirror follows the machine's current tunnel after a reconnect.
            var remote = current() ?? throw new MachineUnreachable(Name, "Board source is not connected");
            var address = new UriBuilder(new Uri(remote.Address, "/socket")) { Scheme = remote.Address.Scheme == "https" ? "wss" : "ws" };
            await socket.ConnectAsync(address.Uri, connection.Token);
            receiver = ReceiveAsync(socket, welcome, subscribed, Wake, connection.Token);
            await SendAsync(socket, json =>
            {
                json.WriteString("t", "hello"); json.WriteNumber("protocol", 1); json.WriteString("token", remote.Token);
            }, connection.Token);
            await welcome.Task.WaitAsync(connection.Token);
            await SendAsync(socket, json => { json.WriteString("t", "sub"); json.WriteNumber("id", 0); json.WriteString("topic", "board"); }, connection.Token);
            await subscribed.Task.WaitAsync(connection.Token);
            ready.TrySetResult(socket);
            Wake();
            catchup = CatchupAsync(socket, wake, connection.Token);
            // Either worker failing closes the connection and fails outstanding calls.
            await await Task.WhenAny(receiver, catchup);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ready.TrySetException(new MachineUnreachable(Name, "Board source is unreachable", error));
        }
        finally
        {
            await connection.CancelAsync();
            socket.Abort();
            var error = new MachineUnreachable(Name, "Board connection closed");
            ready.TrySetException(error);
            foreach (var (id, pending) in _calls)
                if (_calls.TryRemove(id, out _)) pending.TrySetException(error);
            if (receiver is not null) try { await receiver; } catch (Exception) { }
            if (catchup is not null) try { await catchup; } catch (Exception) { }
            if (!_stop.IsCancellationRequested) unreachable();
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, TaskCompletionSource welcome, TaskCompletionSource subscribed,
        Action wake, CancellationToken cancel)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                using var bytes = new MemoryStream();
                WebSocketReceiveResult frame;
                do
                {
                    frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancel);
                    if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("Board socket closed");
                    bytes.Write(buffer, 0, frame.Count);
                    if (bytes.Length > 4 * 1024 * 1024) throw new IOException("Board frame too large");
                } while (!frame.EndOfMessage);
                using var doc = JsonDocument.Parse(bytes.ToArray());
                var json = doc.RootElement;
                var type = json.GetProperty("t").GetString();
                if (type == "welcome") welcome.TrySetResult();
                else if (type == "state") { subscribed.TrySetResult(); wake(); }
                else if (type == "change")
                {
                    // Decoding verifies the same concrete invalidation contract used by the host.
                    json.GetProperty("item").Deserialize(WorkflowJson.Default.BoardChange);
                    wake();
                }
                // Calls count from 1 and the subscription is 0. A reply with no pending call answers one that gave up
                // (a cancelled call gets a "cancelled" error after it left), so nothing waits for it.
                else if (json.TryGetProperty("id", out var id) && id.GetInt64() != 0)
                {
                    if (!_calls.TryRemove(id.GetInt64(), out var pending)) continue;
                    if (type == "result") pending.TrySetResult(json.GetProperty("result").Clone());
                    else if (json.GetProperty("code").GetString() == "failed")
                        pending.TrySetException(new CallFailed(json.GetProperty("message").GetString()!));
                    else pending.TrySetException(new MachineUnreachable(Name, "Board source call failed"));
                }
                else if (type == "error") throw new IOException("Board subscription refused");
            }
        }
        finally
        {
            welcome.TrySetException(new MachineUnreachable(Name, "Board welcome missing"));
            subscribed.TrySetException(new MachineUnreachable(Name, "Board subscription missing"));
        }
    }

    private async Task CatchupAsync(ClientWebSocket socket, SemaphoreSlim wake, CancellationToken cancel)
    {
        while (true)
        {
            await wake.WaitAsync(cancel);
            while (true)
            {
                var after = queries.Applied(Name, cancel);
                var result = await CallAsync(socket, "changes", new BoardChangesArgs(after), WorkflowJson.Default.BoardChangesArgs,
                    WorkflowJson.Default.BoardChangesResult, cancel);
                foreach (var item in result.Events)
                {
                    transitions.Apply(item, Name);
                    if (item.Kind == "answer_delivered")
                        foreach (var question in item.Questions)
                            if (question is { DeliveryState: "delivered", DeliveryTurnId: { } runId })
                                AnswerDelivered?.Invoke(item.Task.CreatorSessionId, runId);
                }
                if (result.NextSeq == result.LatestSeq) break;
                if (result.NextSeq <= after) throw new IOException("Board replay did not advance");
            }
        }
    }

    public async Task<BoardTaskResult> TaskAsync(BoardTaskArgs args, CancellationToken cancel)
    {
        var socket = await ConnectedAsync(cancel);
        var result = await CallAsync(socket, "task", args with { MachineId = "local" }, WorkflowJson.Default.BoardTaskArgs,
            WorkflowJson.Default.BoardTaskResult, cancel);
        return result with
        {
            Task = result.Task with { MachineId = Name },
            Questions = Array.ConvertAll(result.Questions, q => q with { MachineId = Name }),
            Feed = Array.ConvertAll(result.Feed, e => e with
            {
                MachineId = Name,
                Task = e.Task with { MachineId = Name },
                Questions = Array.ConvertAll(e.Questions, q => q with { MachineId = Name })
            })
        };
    }

    public async Task<BoardAnswerResult> AnswerAsync(BoardAnswerArgs args, CancellationToken cancel)
    {
        var socket = await ConnectedAsync(cancel);
        var result = await CallAsync(socket, "answer", args with { MachineId = "local" }, WorkflowJson.Default.BoardAnswerArgs,
            WorkflowJson.Default.BoardAnswerResult, cancel);
        return result with { MachineId = Name };
    }

    private async Task<ClientWebSocket> ConnectedAsync(CancellationToken cancel)
    {
        Reconnect();
        Task<ClientWebSocket> ready;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ready = _ready!.Task;
        }
        return await ready.WaitAsync(cancel);
    }

    private async Task<TResult> CallAsync<TArgs, TResult>(ClientWebSocket socket, string method, TArgs args,
        JsonTypeInfo<TArgs> input, JsonTypeInfo<TResult> output, CancellationToken cancel)
    {
        var id = Interlocked.Increment(ref _id);
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[id] = pending;
        try
        {
            await SendAsync(socket, json =>
            {
                json.WriteString("t", "call"); json.WriteNumber("id", id); json.WriteString("feature", "board");
                json.WriteString("method", method); json.WritePropertyName("args"); JsonSerializer.Serialize(json, args, input);
            }, cancel);
            return (await pending.Task.WaitAsync(cancel)).Deserialize(output)!;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (socket.State == WebSocketState.Open)
                try { await SendAsync(socket, json => { json.WriteString("t", "cancel"); json.WriteNumber("id", id); }, _stop.Token); }
                catch (Exception) { }
            throw;
        }
        catch (Exception error) when (error is WebSocketException or InvalidOperationException or IOException or JsonException)
        { throw new MachineUnreachable(Name, "Board socket failed", error); }
        finally { _calls.TryRemove(id, out _); }
    }

    private async Task SendAsync(ClientWebSocket socket, Action<Utf8JsonWriter> body, CancellationToken cancel)
    {
        using var bytes = new MemoryStream();
        using (var json = new Utf8JsonWriter(bytes)) { json.WriteStartObject(); body(json); json.WriteEndObject(); }
        await _send.WaitAsync(cancel);
        try { await socket.SendAsync(bytes.ToArray(), WebSocketMessageType.Text, true, cancel); }
        finally { _send.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        Task run;
        lock (_gate) { _disposed = true; run = _run; }
        await _stop.CancelAsync();
        await run;
        _stop.Dispose();
        _send.Dispose();
    }
}
