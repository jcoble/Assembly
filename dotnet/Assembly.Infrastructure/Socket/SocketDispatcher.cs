using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
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

    private readonly Dictionary<string, Call> _calls = [];
    // Each topic's state frame for a new subscriber: (sub id, key, context) to the frame.
    private readonly Dictionary<string, Func<long, string?, CallContext, ReadOnlyMemory<byte>>> _topics = [];

    public void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, CallContext, Task<TResult>> handler) =>
        _calls.Add($"{feature}/{method}", new Call(Writes: false, json => json.Deserialize(args),
            async (value, call) => JsonSerializer.SerializeToElement(await handler((TArgs)value, call), result)));

    public void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler)
    {
        var name = $"{feature}/{method}";
        _calls.Add(name, new Call(Writes: true, json => json.Deserialize(args), async (value, call) =>
        {
            // A write cancelled or out of time while it waited in the queue never runs, and one whose outside part ran
            // over commits nothing.
            call.Cancellation.ThrowIfCancellationRequested();
            var database = await handler((TArgs)value, call);
            call.Cancellation.ThrowIfCancellationRequested();
            return JsonSerializer.SerializeToElement(writer.Write(name, database), result);
        }));
    }

    public void Topic<TItem>(string topic, JsonTypeInfo<TItem> item, Func<string?, CallContext, IReadOnlyList<TItem>> state) =>
        _topics.Add(topic, (sub, key, context) => StateFrame(sub, state(key, context), item));

    /// <summary>
    /// Subscribes the socket to a <c>sub</c> frame's topic: its state is read and queued, and the subscription registered,
    /// under the Topics lock. Null when there's nothing to subscribe to; the socket has been answered with an error.
    /// </summary>
    internal TopicSet.Subscription? Subscribe(InFrame frame, long id, SocketConnection connection)
    {
        if (!_topics.TryGetValue(frame.Topic!, out var state))
        {
            connection.Send(Error(id, "not_found", $"there's no topic {frame.Topic}"));
            return null;
        }
        var context = new CallContext(connection.Closed, connection.Id);
        try
        {
            return topics.Subscribe(frame.Topic!, frame.Key, () =>
            {
                var snapshot = state(id, frame.Key, context);
                // Thrown inside the lock, so a state that can't go out subscribes nothing.
                if (snapshot.Length > SocketConnection.MaxFrame)
                    throw new CallFailed($"{frame.Topic}'s state is over 4 MB");
                connection.Send(snapshot);
            }, json => connection.Send(ChangeFrame(id, json)));
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
            // A write joins its socket's queue now, in arrival order; a read runs off the receive loop, so one that
            // blocks doesn't hold up the frames after it.
            var result = call.Writes
                ? await connection.QueueWriteAsync(() => call.RunAsync(args, context))
                : await Task.Run(() => call.RunAsync(args, context));
            var answer = Frame(new ResultFrame(id, result), SocketJson.Default.ResultFrame);
            return Bounded(answer, id);
        }
        catch (CallFailed error)
        {
            return Error(id, "failed", error.Message);
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

    internal static ReadOnlyMemory<byte> Frame<T>(T frame, JsonTypeInfo<T> type) => JsonSerializer.SerializeToUtf8Bytes(frame, type);

    private static ReadOnlyMemory<byte> Error(long id, string code, string message) =>
        Bounded(Frame(new ErrorFrame(id, code, message), SocketJson.Default.ErrorFrame), id);

    // No frame goes out over 4 MB (plan 5.1): an answer over it answers a short failed instead.
    private static ReadOnlyMemory<byte> Bounded(ReadOnlyMemory<byte> frame, long id) =>
        frame.Length <= SocketConnection.MaxFrame
            ? frame
            : Frame(new ErrorFrame(id, "failed", "the answer is over 4 MB"), SocketJson.Default.ErrorFrame);

    // {"t":"state","sub":N,"items":[...]}, written directly, since the items' type is the topic's.
    private static ReadOnlyMemory<byte> StateFrame<TItem>(long sub, IReadOnlyList<TItem> items, JsonTypeInfo<TItem> type)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("t", "state");
            json.WriteNumber("sub", sub);
            json.WriteStartArray("items");
            foreach (var item in items)
                JsonSerializer.Serialize(json, item, type);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return buffer.WrittenMemory;
    }

    // {"t":"change","sub":N,"op":"upsert","item":...}: the item is the JSON the writer published. Only upsert until a
    // feature deletes.
    private static ReadOnlyMemory<byte> ChangeFrame(long sub, string item)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("t", "change");
            json.WriteNumber("sub", sub);
            json.WriteString("op", "upsert");
            json.WritePropertyName("item");
            json.WriteRawValue(item, skipInputValidation: true);
            json.WriteEndObject();
        }
        return buffer.WrittenMemory;
    }

    private sealed record Call(bool Writes, Func<JsonElement, object?> ReadArgs, Func<object, CallContext, Task<JsonElement>> RunAsync);
}
