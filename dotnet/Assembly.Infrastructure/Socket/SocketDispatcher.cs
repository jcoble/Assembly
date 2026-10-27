using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Infrastructure.Socket;

/// <summary>
/// Every feature's calls, and the same steps around each one, so no feature can skip one (plan 5.2): find the call
/// (<c>not_found</c>), read its args (<c>bad_args</c>), log it, give it a token from its <c>cancel</c>, the 30 s limit
/// and, for a read, its socket closing, then run it: a read at once, a write on its socket's write queue, its database
/// part inside <see cref="IAtomicWriter.Write"/>. The answer is its result, or <c>failed</c>, <c>cancelled</c> or
/// <c>timeout</c>; an exception other than <see cref="CallFailed"/> is logged at Error.
/// </summary>
internal sealed class SocketDispatcher(IAtomicWriter writer, TimeProvider time, ILogger log) : ICallRegistry
{
    // No call runs longer, saves included (plan 5.1). Long work is started by a call and reports on a topic.
    internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, Call> _calls = [];

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
            var database = await handler((TArgs)value, call);
            return JsonSerializer.SerializeToElement(writer.Write(name, database), result);
        }));
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
            var result = call.Writes ? await connection.QueueWriteAsync(() => call.RunAsync(args, context)) : await call.RunAsync(args, context);
            return Frame(new ResultFrame(id, result), SocketJson.Default.ResultFrame);
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
        Frame(new ErrorFrame(id, code, message), SocketJson.Default.ErrorFrame);

    private sealed record Call(bool Writes, Func<JsonElement, object?> ReadArgs, Func<object, CallContext, Task<JsonElement>> RunAsync);
}
