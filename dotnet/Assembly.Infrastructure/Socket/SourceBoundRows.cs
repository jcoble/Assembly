using System.Buffers;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Rows;

namespace Assembly.Infrastructure.Socket;

/// <summary>Owns exactly one source-bound rows subscription, including its already queued frames.</summary>
internal sealed class SourceBoundRows : ISocketSubscription, IExpectedSourceGuard
{
    private readonly RowSourceBinding _source;
    private readonly SocketConnection _connection;
    private readonly long _id;
    private readonly CancellationTokenSource _closed;
    private readonly CancellationToken _token;
    private readonly CancellationTokenRegistration _invalidated;
    private ISocketSubscription? _inner;
    private int _disposed, _failed;
    public SourceBoundRows(RowSourceBinding source, SocketConnection connection, long id)
    {
        (_source, _connection, _id) = (source, connection, id);
        _closed = CancellationTokenSource.CreateLinkedTokenSource(connection.Closed, source.Changed);
        _token = _closed.Token;
        _invalidated = source.Changed.UnsafeRegister(_ => Fail(), null);
    }
    public CancellationToken Closed => _token;
    internal RowSourceBinding Source => _source;
    public CancellationToken Changed => _token;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public void Attach(ISocketSubscription inner)
    {
        _inner = inner;
        if (_token.IsCancellationRequested) inner.Dispose();
    }
    public void EnsureCurrent()
    {
        try { _token.ThrowIfCancellationRequested(); _source.EnsureCurrent(); }
        catch (Exception error) when (error is CallFailed or OperationCanceledException)
        {
            if (_source.Changed.IsCancellationRequested || !Closed.IsCancellationRequested) Fail();
            throw;
        }
    }
    public void Start(long? after) => Completion = RunAsync(after);
    private async Task RunAsync(long? after)
    {
        try
        {
            await foreach (var frame in _source.WatchAsync(after, Closed))
            {
                EnsureCurrent();
                var type = frame.GetProperty("t").GetString();
                if (type is not ("state" or "rows")) throw new CallFailed("Invalid row stream.");
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    foreach (var property in frame.EnumerateObject())
                        if (property.Name == "sub") writer.WriteNumber("sub", _id);
                        else property.WriteTo(writer);
                    writer.WriteEndObject();
                }
                EnsureCurrent();
                _connection.Send(buffer.WrittenMemory, this);
            }
            if (!Closed.IsCancellationRequested) Fail();
        }
        catch (Exception error) when (error is CallFailed or OperationCanceledException or IOException)
        {
            if (!Closed.IsCancellationRequested || _source.Changed.IsCancellationRequested) Fail();
        }
        finally { Dispose(); }
    }
    private void Fail()
    {
        if (Interlocked.Exchange(ref _failed, 1) == 0 && !_connection.Closed.IsCancellationRequested)
            _connection.Send(SocketDispatcher.Frame(new ErrorFrame(_id, "source_changed", "The selected row source changed."), SocketJson.Default.ErrorFrame));
        Dispose();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _closed.Cancel();
        _inner?.Dispose();
        _invalidated.Unregister();
        _source.Dispose();
        _closed.Dispose();
    }
}

/// <summary>The sender owns a read's source lease until that one reply has left the queue or been refused.</summary>
internal sealed class SourceBoundReply(long id, IExpectedSourceGuard source) : ISocketSubscription, IExpectedSourceGuard
{
    public CancellationToken Closed => source.Changed;
    public CancellationToken Changed => source.Changed;
    public Task Completion => Task.CompletedTask;
    public void EnsureCurrent() => source.EnsureCurrent();
    public ReadOnlyMemory<byte> Refused => SocketDispatcher.Frame(
        new ErrorFrame(id, "source_changed", "The selected row source changed."), SocketJson.Default.ErrorFrame);
    public void Dispose() => source.Dispose();
}
