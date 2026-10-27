using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Assembly.Application.Features.Terminal;
using Assembly.Infrastructure.Agents;

namespace Assembly.Infrastructure.Terminal;

/// <summary>
/// The master side of a started PTY, and the process on it, which leads its own session and group. A thread of its own
/// reads the master, since a read blocks for the terminal's whole life, and hands each read over through a small
/// channel, so a reader that falls behind holds the program up rather than the backend's memory. That thread owns
/// the master and closes it when it's done: closing it under a blocked read could hand its number to another file.
/// </summary>
internal sealed class UnixPty : IPty
{
    private const int ReadSize = 16 * 1024;

    private readonly int _master;
    private readonly Process _process;
    private readonly Channel<byte[]> _output = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true });
    private readonly Lock _lock = new();
    private int _slave;
    private bool _disposed;
    private byte[] _left = [];
    private int _leftAt;

    public UnixPty(int master, int slave, Process process)
    {
        _master = master;
        _slave = slave;
        _process = process;
        Pid = process.Id;
        Exited = WaitAsync();
        new Thread(ReadMaster) { IsBackground = true, Name = $"pty {Pid}" }.Start();
    }

    public int Pid { get; }

    public Task<int> Exited { get; }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_leftAt == _left.Length)
        {
            if (!await _output.Reader.WaitToReadAsync(cancellationToken) || !_output.Reader.TryRead(out var next))
                return 0;
            (_left, _leftAt) = (next, 0);
        }
        var count = Math.Min(buffer.Length, _left.Length - _leftAt);
        _left.AsMemory(_leftAt, count).CopyTo(buffer);
        _leftAt += count;
        return count;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // A write blocks while the program isn't reading and the terminal's input is full, so it runs off the caller.
        await Task.Run(() =>
        {
            var left = bytes.Span;
            while (left.Length > 0)
            {
                var wrote = PtyNative.write(_master, ref MemoryMarshal.GetReference(left), left.Length);
                if (wrote < 0 && Marshal.GetLastPInvokeError() != PtyNative.EIntr)
                    throw PtyNative.Failed("writing to the terminal");
                left = left[(int)Math.Max(wrote, 0)..];
            }
        }, cancellationToken);
    }

    public void Resize(int cols, int rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PtyNative.SetSize(_master, cols, rows);
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;
        }
        // The shell's group, and the groups of the jobs it started: an interactive shell gives each job its own.
        ProcessGroup.Kill(Pid);
        CloseSlave();
        _output.Writer.TryComplete();
        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<int> WaitAsync()
    {
        await _process.WaitForExitAsync();
        var code = _process.ExitCode;
        // Now the master reads end of file once whatever the program left running lets go of the terminal too.
        CloseSlave();
        return code;
    }

    private void CloseSlave()
    {
        lock (_lock)
        {
            if (_slave >= 0)
                _ = PtyNative.close(_slave);
            _slave = -1;
        }
    }

    private void ReadMaster()
    {
        var buffer = new byte[ReadSize];
        try
        {
            while (true)
            {
                var read = PtyNative.read(_master, ref buffer[0], buffer.Length);
                if (read < 0 && Marshal.GetLastPInvokeError() == PtyNative.EIntr)
                    continue;
                // Linux answers EIO, not 0, once the last slave has closed: either way the output has ended.
                if (read <= 0)
                    break;
                var chunk = buffer[..(int)read];
                while (!_output.Writer.TryWrite(chunk))
                    if (!_output.Writer.WaitToWriteAsync().AsTask().GetAwaiter().GetResult())
                        return;
            }
        }
        finally
        {
            _output.Writer.TryComplete();
            _ = PtyNative.close(_master);
        }
    }
}
