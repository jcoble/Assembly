using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Acp;

namespace Assembly.Application.Common.Acp.JsonRpc;

/// <summary>
/// JSON-RPC 2.0 over newline-delimited JSON (one message per line), the transport ACP uses on stdio.
/// One read loop dispatches what arrives; writes are serialized so lines never interleave.
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly IJsonRpcTarget _target;
    private readonly TranscriptLog _transcript;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _stopping = new();
    private volatile Exception? _closedReason;
    private long _lastId;

    public JsonRpcConnection(Stream input, Stream output, IJsonRpcTarget target, TranscriptLog transcript)
    {
        _input = input;
        _output = output;
        _target = target;
        _transcript = transcript;
        Completion = Task.Run(ReadLoopAsync);
    }

    /// <summary>Completes when the read loop has ended and every pending request has failed.</summary>
    public Task Completion { get; }

    public async Task<JsonElement> RequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _lastId);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            if (_closedReason is { } closed)
                throw new IOException("The connection is closed.", closed);

            await WriteAsync(json =>
            {
                json.WriteNumber("id", id);
                json.WriteString("method", method);
                json.WritePropertyName("params");
                parameters.WriteTo(json);
            }, cancellationToken);

            return await reply.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, JsonElement parameters, CancellationToken cancellationToken) =>
        WriteAsync(json =>
        {
            json.WriteString("method", method);
            json.WritePropertyName("params");
            parameters.WriteTo(json);
        }, cancellationToken);

    private async Task ReadLoopAsync()
    {
        Exception reason = new IOException("The agent closed its output.");
        try
        {
            using var reader = new StreamReader(_input, new UTF8Encoding(false));
            while (await reader.ReadLineAsync(_stopping.Token) is { } line)
            {
                if (line.Length == 0)
                    continue;
                _transcript.Received(line);
                Dispatch(line);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            reason = new ObjectDisposedException(nameof(JsonRpcConnection));
        }
        catch (Exception ex)
        {
            reason = ex;
        }

        _closedReason = reason;
        foreach (var reply in _pending.Values)
            reply.TrySetException(new IOException("The connection closed before the agent answered.", reason));
        _target.OnClosed(reason);
    }

    private void Dispatch(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return; // Not JSON. It's already in the transcript, which is where we'd look.
        }

        using (document)
        {
            var root = document.RootElement;
            var hasMethod = root.TryGetProperty("method", out var method);
            var hasId = root.TryGetProperty("id", out var id);
            var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;

            if (hasMethod && hasId)
                _ = AnswerAsync(id.Clone(), method.GetString() ?? "", parameters);
            else if (hasMethod)
                _target.OnNotification(method.GetString() ?? "", parameters);
            else if (hasId && id.TryGetInt64(out var number) && _pending.TryGetValue(number, out var reply))
                Settle(reply, root);
        }
    }

    private static void Settle(TaskCompletionSource<JsonElement> reply, JsonElement response)
    {
        if (response.TryGetProperty("error", out var error))
            reply.TrySetException(JsonRpcException.FromError(error));
        else
            reply.TrySetResult(response.TryGetProperty("result", out var result) ? result.Clone() : default);
    }

    // Runs off the read loop, so a request that waits (a permission prompt) doesn't stall the stream.
    private async Task AnswerAsync(JsonElement id, string method, JsonElement parameters)
    {
        try
        {
            var result = await _target.OnRequestAsync(method, parameters, _stopping.Token);
            await WriteAsync(json =>
            {
                json.WritePropertyName("id");
                id.WriteTo(json);
                json.WritePropertyName("result");
                result.WriteTo(json);
            }, CancellationToken.None);
        }
        catch (JsonRpcException ex)
        {
            await TryWriteErrorAsync(id, ex.Code, ex.Message);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await TryWriteErrorAsync(id, JsonRpcException.InternalError, ex.Message);
        }
    }

    private async Task TryWriteErrorAsync(JsonElement id, int code, string message)
    {
        try
        {
            await WriteAsync(json =>
            {
                json.WritePropertyName("id");
                id.WriteTo(json);
                json.WriteStartObject("error");
                json.WriteNumber("code", code);
                json.WriteString("message", message);
                json.WriteEndObject();
            }, CancellationToken.None);
        }
        catch (Exception)
        {
            // The pipe is gone; there's no one left to tell.
        }
    }

    private async Task WriteAsync(Action<Utf8JsonWriter> body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            body(json);
            json.WriteEndObject();
        }

        var bytes = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
        _transcript.Sent(Encoding.UTF8.GetString(bytes.Span));

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _output.WriteAsync(bytes, cancellationToken);
            await _output.WriteAsync(NewLine, cancellationToken);
            await _output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Stops reading. A blocked pipe read may not notice cancellation, so the owner closes the
    /// agent process first; its exit ends the stream and lets the loop finish.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        try
        {
            await Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
        }
        _stopping.Dispose();
    }
}
