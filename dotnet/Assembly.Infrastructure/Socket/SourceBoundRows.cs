using System.Buffers;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Infrastructure.Engine;

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

/// <summary>The existing Engine message stream fenced by one captured source, including queued frames and ACKs.</summary>
internal sealed class SourceBoundEngine : IEngineSubscription, IExpectedSourceGuard
{
    private readonly IExpectedSourceGuard _source;
    private readonly SocketConnection _connection;
    private readonly long _id;
    private readonly CancellationTokenSource _closed;
    private readonly CancellationTokenRegistration _invalidated;
    private CancellationTokenRegistration _innerClosed;
    private IEngineSubscription? _inner;
    private int _disposed, _failed;
    private SourceBoundEngine(IExpectedSourceGuard source, SocketConnection connection, long id)
    {
        (_source, _connection, _id) = (source, connection, id);
        _closed = CancellationTokenSource.CreateLinkedTokenSource(connection.Closed, source.Changed);
        Closed = _closed.Token;
        _invalidated = source.Changed.UnsafeRegister(_ => Fail(), null);
        if (Closed.IsCancellationRequested) _invalidated.Unregister();
    }
    public CancellationToken Closed { get; }
    public CancellationToken Changed => Closed;
    public Task Completion => _inner?.Completion ?? Task.CompletedTask;
    public void EnsureCurrent()
    {
        try { Closed.ThrowIfCancellationRequested(); _source.EnsureCurrent(); }
        catch (Exception error) when (error is CallFailed or OperationCanceledException) { Fail(); throw; }
    }
    private bool Current()
    {
        try { EnsureCurrent(); return true; }
        catch (Exception error) when (error is CallFailed or OperationCanceledException) { return false; }
    }
    private void Attach(IEngineSubscription inner)
    {
        _inner = inner;
        _innerClosed = inner.Closed.UnsafeRegister(_ => Dispose(), null);
        if (Closed.IsCancellationRequested) { _innerClosed.Unregister(); inner.Dispose(); }
    }
    public void Acknowledge(long through) { if (Current()) _inner!.Acknowledge(through); }
    public void Next() { if (Current()) _inner!.Next(); }
    private void Fail()
    {
        if (Interlocked.Exchange(ref _failed, 1) == 0 && !_connection.Closed.IsCancellationRequested)
            _connection.Send(SocketDispatcher.Frame(new ErrorFrame(_id, "source_changed", "The selected message source changed."), SocketJson.Default.ErrorFrame));
        Dispose();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _closed.Cancel(); _innerClosed.Unregister(); _inner?.Dispose(); _invalidated.Unregister(); _source.Dispose(); _closed.Dispose();
    }

    internal static async Task<ISocketSubscription?> OpenAsync(InFrame frame, long id, SocketConnection connection,
        EngineSocketStream? engine, IRowSourceBindings? sources, TimeProvider time)
    {
        IExpectedSourceGuard? source = null; SourceBoundEngine? bound = null;
        try
        {
            var expected = frame.ExpectedSource ?? throw new CallFailed("The message source is required.", "source_changed");
            if (engine is null || sources is null || connection.Peer is not OwnerPeer || connection.Bulk || connection.Terminal ||
                frame.Key is null || string.IsNullOrWhiteSpace(frame.Key) || frame.Machine != expected.Machine)
                throw new CallFailed("Source-bound messages are unavailable.", "source_changed");
            using var limit = new CancellationTokenSource(SocketDispatcher.Limit, time);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(connection.Closed, limit.Token);
            source = await sources.CaptureEngineAsync(expected, stop.Token);
            source.EnsureCurrent();
            var owned = new SourceBoundEngine(source, connection, id); bound = owned; owned.EnsureCurrent();
            var inner = engine.Subscribe(id, "messages", frame.Key, frame.After, (text, from) =>
            {
                if (owned.Current()) connection.Send(System.Text.Encoding.UTF8.GetBytes(text), from is null ? null : owned);
            }, owned.Closed, connection.Abort, frame.At, frame.Machine);
            owned.Attach(inner); return owned;
        }
        catch (Exception error) when (error is CallFailed or OperationCanceledException or ArgumentException or KeyNotFoundException)
        {
            bound?.Dispose(); if (bound is null) source?.Dispose();
            var code = error is ArgumentException ? "bad_args" : error is CallFailed failed ? failed.Reason ?? "failed" : "source_changed";
            connection.Send(SocketDispatcher.Frame(new ErrorFrame(id, code, "The selected message source is unavailable."), SocketJson.Default.ErrorFrame));
            return null;
        }
    }
}
