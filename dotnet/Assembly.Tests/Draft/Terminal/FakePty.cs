using System.Threading.Channels;
using Assembly.Application.Features.Terminal;

namespace Assembly.Tests.Draft.Terminal;

/// <summary>A PTY the test drives: what the program "writes" is <see cref="Emit"/>, its end is <see cref="End"/>.</summary>
internal sealed class FakePty(PtyRequest request, int pid) : IPty
{
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource<PtyExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PtyRequest Request { get; } = request;
    public List<byte[]> Written { get; } = [];
    public List<(int Cols, int Rows)> Resizes { get; } = [];
    public bool Disposed { get; private set; }

    public int Pid { get; } = pid;
    public Task<PtyExit> Exited => _exited.Task;

    public void Emit(params byte[] bytes) => _output.Writer.TryWrite(bytes);

    /// <summary>The program ends: its output closes, then its exit.</summary>
    public void End(PtyExit exit)
    {
        _output.Writer.TryComplete();
        _exited.TrySetResult(exit);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (!await _output.Reader.WaitToReadAsync(cancellationToken))
            return 0;
        var bytes = await _output.Reader.ReadAsync(cancellationToken);
        bytes.CopyTo(buffer);
        return bytes.Length;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        Written.Add(bytes.ToArray());
        return ValueTask.CompletedTask;
    }

    public void Resize(int cols, int rows) => Resizes.Add((cols, rows));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        End(new PtyExit(null, 9));
        return ValueTask.CompletedTask;
    }
}
