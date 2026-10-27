using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Infrastructure.Socket;

/// <summary>
/// Every feature's calls, and the same steps around each one, so no feature can skip one (plan 5.2): find the call
/// (<c>not_found</c>), read its args (<c>bad_args</c>), log it, give it a token from its <c>cancel</c>, the 30 s limit
/// and, for a read, its socket closing, then run it: a read at once, a write on its socket's write queue, its database
/// part inside <see cref="IAtomicWriter.Write"/>. The answer is its result, or <c>failed</c>, <c>cancelled</c> or
/// <c>timeout</c>; an exception other than <see cref="CallFailed"/> is logged at Error. A topic's <c>sub</c> gets its
/// state, then its changes (5.7).
/// </summary>
internal sealed class SocketDispatcher(IAtomicWriter writer, TopicSet topics, TimeProvider time, ILogger log) : ICallRegistry
{
    // No call runs longer, saves included (plan 5.1). Long work is started by a call and reports on a topic.
    internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static readonly JsonElement Null = JsonDocument.Parse("null").RootElement.Clone();

    private readonly Dictionary<string, Call> _calls = [];
    // Each topic, by name.
    private readonly Dictionary<string, TopicFrames> _topics = [];
    private readonly List<Action<string>> _closedHooks = [];

    public void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, CallContext, Task<TResult>> handler) =>
        _calls.Add($"{feature}/{method}", new Call(Writes: false, json => json.Deserialize(args),
            async (value, call) => JsonSerializer.SerializeToElement(await handler((TArgs)value, call), result)));

    public void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler, Func<TArgs, string?>? order = null,
        Action<TArgs, CallContext>? cancelled = null)
    {
        var name = $"{feature}/{method}";
        _calls.Add(name, new Call(Writes: true, json => json.Deserialize(args), async (value, call) =>
        {
            try
            {
                // A write cancelled or out of time while it waited in the queue never runs, and one whose outside part
                // ran over commits nothing.
                call.Cancellation.ThrowIfCancellationRequested();
                var database = await handler((TArgs)value, call);
                call.Cancellation.ThrowIfCancellationRequested();
                return JsonSerializer.SerializeToElement(writer.Write(name, database), result);
            }
            catch (OperationCanceledException) when (call.Cancellation.IsCancellationRequested && cancelled is not null)
            {
                cancelled((TArgs)value, call);
                throw;
            }
        }, order is null ? null : value => order((TArgs)value)));
    }

    public void Save<TValue>(string feature, JsonTypeInfo<TValue> value, Action<string, TValue, IWriteScope> save)
    {
        // The page holds a save's echo by its topic and key, so the topic has to exist.
        if (!_topics.ContainsKey(feature))
            throw new InvalidOperationException($"{feature} saves, but there's no topic {feature} for their changes");
        _calls.Add($"{feature}/set", new Call(Writes: true, json => ReadSave(json, value), (args, call) =>
        {
            var (key, item) = ((string, TValue))args;
            call.Cancellation.ThrowIfCancellationRequested();
            try
            {
                writer.Write($"{feature}/set {key}", scope =>
                {
                    save(key, item, scope);
                    return 0;
                });
            }
            catch (Exception error) when (error is not CallFailed)
            {
                // Its message could hold the value, which never goes into a log (some settings hold tokens). The call's
                // own cancel was checked before the write, so anything here is the save's.
                log.LogError("{Save} failed with {Type}", $"{feature}/set {key}", error.GetType().Name);
                throw new CallFailed($"{feature}/set {key} failed");
            }
            return Task.FromResult(Null);
        }));
    }

    public void Topic<TItem>(string topic, JsonTypeInfo<TItem> item, Func<TItem, string> key,
        Func<string?, CallContext, IReadOnlyList<TItem>> state) =>
        _topics.Add(topic, new TopicFrames(NeedsKey: false, (sub, frame, context) =>
            ([StateFrame(sub, state(frame.Key, context), item, key)], (changed, json) => ChangeFrame(sub, changed, json))));

    public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state,
        Action<string, CallContext>? opened = null) =>
        _topics.Add(topic, new TopicFrames(NeedsKey: true, (sub, frame, context) =>
        {
            var (seq, rows) = state(frame.Key!, frame.After, context);
            // A row committed before the snapshot but published after it is already in it (5.7): only rows past the
            // snapshot's highest seq go out.
            var sent = rows.Count > 0 ? Seq(rows[^1]) : seq;
            return ([RowsStateFrame(sub, seq), .. rows.Select(row => RowsFrame(sub, row))],
                (_, row) => Seq(row) > sent ? RowsFrame(sub, row) : (ReadOnlyMemory<byte>?)null);
        }, opened));

    /// <summary>
    /// Subscribes the socket to a <c>sub</c> frame's topic: its state is read and queued, and the subscription registered,
    /// under the Topics lock. Null when there's nothing to subscribe to; the socket has been answered with an error.
    /// </summary>
    internal TopicSet.Subscription? Subscribe(InFrame frame, long id, SocketConnection connection)
    {
        if (!_topics.TryGetValue(frame.Topic!, out var topic))
        {
            connection.Send(Error(id, "not_found", $"there's no topic {frame.Topic}"));
            return null;
        }
        if (topic.NeedsKey && frame.Key is null)
        {
            connection.Send(Error(id, "bad_args", $"{frame.Topic} needs a key"));
            return null;
        }
        var context = new CallContext(connection.Closed, connection.Id);
        TopicSet.Subscription? self = null;
        Func<string?, string, ReadOnlyMemory<byte>?>? change = null;
        try
        {
            var opened = topics.Subscribe(frame.Topic!, frame.Key, subscription =>
            {
                self = subscription;
                (var snapshot, change) = topic.Open(id, frame, context);
                // Thrown inside the lock, so a state that can't go out subscribes nothing.
                if (snapshot.Any(part => part.Length > SocketConnection.MaxFrame))
                    throw new CallFailed($"{frame.Topic}'s state is over 4 MB");
                foreach (var part in snapshot)
                    connection.Send(part, subscription);
            }, (key, json) =>
            {
                if (change!(key, json) is { } changed)
                    connection.Send(changed, self);
            });
            // Out of the Topics lock: it may take the hub's lock, which comes before Topics, or start work. The sub is
            // registered by now, so one that throws is logged and the sub stays good, as the socket's to unsubscribe.
            try
            {
                topic.Opened?.Invoke(frame.Key!, context);
            }
            catch (Exception error)
            {
                log.LogError(error, "topic {Topic} opened {Key} but its follow-up failed", frame.Topic, frame.Key);
            }
            return opened;
        }
        catch (CallFailed error)
        {
            connection.Send(Error(id, "failed", error.Message));
            return null;
        }
        catch (OperationCanceledException) when (connection.Closed.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error)
        {
            log.LogError(error, "topic {Topic} failed", frame.Topic);
            connection.Send(Error(id, "failed", $"{frame.Topic} failed"));
            return null;
        }
    }

    internal void Unsubscribe(TopicSet.Subscription subscription) => topics.Unsubscribe(subscription);

    public void Closed(Action<string> connection) => _closedHooks.Add(connection);

    /// <summary>Runs every <see cref="Closed"/> hook for a socket that closed, once its writes have all run.</summary>
    internal void ConnectionClosed(string connection)
    {
        foreach (var hook in _closedHooks)
        {
            try
            {
                hook(connection);
            }
            catch (Exception error)
            {
                log.LogError(error, "a close hook failed on {Connection}", connection);
            }
        }
    }

    /// <summary>Runs one <c>call</c> frame to its answer. <paramref name="cancelled"/> is its <c>cancel</c>.</summary>
    internal async Task<ReadOnlyMemory<byte>> AnswerAsync(InFrame frame, long id, SocketConnection connection, CancellationToken cancelled)
    {
        var name = $"{frame.Feature}/{frame.Method}";
        if (!_calls.TryGetValue(name, out var call))
            return Error(id, "not_found", $"there's no call {name}");
        object args;
        try
        {
            args = call.ReadArgs(frame.Args) ?? throw new JsonException("args is null");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return Error(id, "bad_args", $"{name}: {error.Message}");
        }

        using var scope = log.BeginScope("{Call} {Id} on {Connection}", name, id, connection.Id);
        log.LogDebug("call {Call}", name);
        using var limit = new CancellationTokenSource(Limit, time);
        // A write isn't cancelled by its socket closing: once queued, it commits (plan 5.9).
        using var token = call.Writes
            ? CancellationTokenSource.CreateLinkedTokenSource(cancelled, limit.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(cancelled, limit.Token, connection.Closed);
        var context = new CallContext(token.Token, connection.Id);
        try
        {
            // A write joins its key's queue, or else its socket's, now, in arrival order; a read runs off the receive
            // loop, so one that blocks doesn't hold up the frames after it.
            var result = call.Writes
                ? await connection.QueueWriteAsync(call.Order?.Invoke(args), () => call.RunAsync(args, context))
                : await Task.Run(() => call.RunAsync(args, context));
            var answer = Frame(new ResultFrame(id, result), SocketJson.Default.ResultFrame);
            return Bounded(answer, id);
        }
        catch (CallFailed error)
        {
            return Error(id, "failed", error.Message);
        }
        catch (MachineUnreachable error)
        {
            return Bounded(Frame(new ErrorFrame(id, "unreachable", error.Message, error.Machine), SocketJson.Default.ErrorFrame), id);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            return Error(id, "timeout", $"{name} ran over {Limit.TotalSeconds:0} s");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Error(id, "cancelled", $"{name} was cancelled");
        }
        catch (Exception error)
        {
            log.LogError(error, "call {Call} failed", name);
            return Error(id, "failed", $"{name} failed");
        }
    }

    /// <summary>
    /// Frames carry text as it is: escaped, an accent or apostrophe takes six bytes, so a big answer of such text would
    /// pass the 4 MB frame limit. The frames are read as JSON, never put into HTML.
    /// </summary>
    private static readonly JsonWriterOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static ReadOnlyMemory<byte> Frame<T>(T frame, JsonTypeInfo<T> type)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, Json))
            JsonSerializer.Serialize(json, frame, type);
        return buffer.WrittenMemory;
    }

    private static ReadOnlyMemory<byte> Error(long id, string code, string message) =>
        Bounded(Frame(new ErrorFrame(id, code, message), SocketJson.Default.ErrorFrame), id);

    // No frame goes out over 4 MB (plan 5.1): an answer over it answers a short failed instead.
    private static ReadOnlyMemory<byte> Bounded(ReadOnlyMemory<byte> frame, long id) =>
        frame.Length <= SocketConnection.MaxFrame
            ? frame
            : Frame(new ErrorFrame(id, "failed", "the answer is over 4 MB"), SocketJson.Default.ErrorFrame);

    // {"t":"state","sub":N,"items":[{"key":K,"item":...}]}, written directly, since the items' type is the topic's. Each
    // entry has the key and item a change frame has, so the page applies both the same way.
    private static ReadOnlyMemory<byte> StateFrame<TItem>(long sub, IReadOnlyList<TItem> items, JsonTypeInfo<TItem> type, Func<TItem, string> key)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, Json))
        {
            json.WriteStartObject();
            json.WriteString("t", "state");
            json.WriteNumber("sub", sub);
            json.WriteStartArray("items");
            foreach (var item in items)
            {
                json.WriteStartObject();
                json.WriteString("key", key(item));
                json.WritePropertyName("item");
                JsonSerializer.Serialize(json, item, type);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return buffer.WrittenMemory;
    }

    // A save's args, {key, value}, read by hand: the value's type is the feature's.
    private static (string, TValue) ReadSave<TValue>(JsonElement json, JsonTypeInfo<TValue> value) =>
        json.TryGetProperty("key", out var key) && key.GetString() is { } name
            && json.TryGetProperty("value", out var item) && item.Deserialize(value) is { } read
            ? (name, read)
            : throw new JsonException("a save takes a key and a value");

    // {"t":"change","sub":N,"op":"upsert","key":K,"item":...}: the key it was published with, and the JSON the writer
    // published. Only upsert until a feature deletes.
    private static ReadOnlyMemory<byte> ChangeFrame(long sub, string? key, string item)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, Json))
        {
            json.WriteStartObject();
            json.WriteString("t", "change");
            json.WriteNumber("sub", sub);
            json.WriteString("op", "upsert");
            json.WriteString("key", key);
            json.WritePropertyName("item");
            json.WriteRawValue(item, skipInputValidation: true);
            json.WriteEndObject();
        }
        return buffer.WrittenMemory;
    }

    // {"t":"state","sub":N,"seq":S,"items":[]}: the rows frames that follow continue from seq S.
    private static ReadOnlyMemory<byte> RowsStateFrame(long sub, long seq)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, Json))
        {
            json.WriteStartObject();
            json.WriteString("t", "state");
            json.WriteNumber("sub", sub);
            json.WriteNumber("seq", seq);
            json.WriteStartArray("items");
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return buffer.WrittenMemory;
    }

    // A published row's seq.
    private static long Seq(string row)
    {
        var json = new Utf8JsonReader(Encoding.UTF8.GetBytes(row));
        json.Read();
        while (json.Read() && json.TokenType == JsonTokenType.PropertyName)
        {
            var seq = json.ValueTextEquals("seq");
            json.Read();
            if (seq)
                return json.GetInt64();
            json.Skip();
        }
        throw new InvalidOperationException("a row without a seq");
    }

    // {"t":"rows","sub":N,"row":...}: one row, as the rows topic published it.
    private static ReadOnlyMemory<byte> RowsFrame(long sub, string row)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, Json))
        {
            json.WriteStartObject();
            json.WriteString("t", "rows");
            json.WriteNumber("sub", sub);
            json.WritePropertyName("row");
            json.WriteRawValue(row, skipInputValidation: true);
            json.WriteEndObject();
        }
        return buffer.WrittenMemory;
    }

    // A topic's frames for one new subscription, (sub id, sub frame, context): its state, and what each change it's
    // published becomes, (key, JSON) to a frame, or null for none.
    private sealed record TopicFrames(bool NeedsKey,
        Func<long, InFrame, CallContext, (IReadOnlyList<ReadOnlyMemory<byte>> State, Func<string?, string, ReadOnlyMemory<byte>?> Change)> Open,
        Action<string, CallContext>? Opened = null);

    private sealed record Call(bool Writes, Func<JsonElement, object?> ReadArgs, Func<object, CallContext, Task<JsonElement>> RunAsync,
        Func<object, string?>? Order = null);
}
