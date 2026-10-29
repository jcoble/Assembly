using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using Assembly.Application.Features.Terminal;
using Assembly.Infrastructure.Agents;

namespace Assembly.Infrastructure.Terminal;

/// <summary>
/// The master side of a started PTY, and the process on it, which leads its own session and group. A thread of its own
/// reads the master, since a read blocks for the terminal's whole life, and hands each read over through a small
/// channel, so a reader that falls behind holds the program up rather than the backend's memory. That thread owns
/// the master's last reference: the handle counts each write and resize too, so its number is only closed, and free
/// for another file, once nothing is using it.
/// </summary>
internal sealed class UnixPty : IPty
{
    private const int ReadSize = 16 * 1024;
    private const int WriteSize = 4 * 1024;

    private readonly SafeFileHandle _master;
    private readonly Process _process;
    private readonly Channel<byte[]> _output = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true });
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _writing = new(1);
    private readonly Thread _reader;
    private int _slave;
    private bool _disposed;
    private volatile bool _ended;
    private byte[] _left = [];
    private int _leftAt;

    public UnixPty(int master, int slave, Process process)
    {
        _master = new SafeFileHandle(master, ownsHandle: true);
        _slave = slave;
        _process = process;
        Pid = process.Id;
        Exited = WaitAsync();
        // It ends once the process group is gone and the master reads end of file; it closes the master then.
        _reader = new Thread(ReadMaster) { IsBackground = true, Name = $"pty {Pid}" };
        _reader.Start();
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
        await _writing.WaitAsync(cancellationToken);
        try
        {
            // The worker only calls nonblocking write. Await it through cancellation too, so no native writer
            // survives this request; the next writer cannot interleave with its accepted prefix.
            await Task.Run(() => Using(fd =>
            {
                var left = bytes.Span;
                while (!left.IsEmpty)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var wrote = PtyNative.write(fd, ref MemoryMarshal.GetReference(left), Math.Min(left.Length, WriteSize));
                    if (wrote < 0)
                    {
                        var error = Marshal.GetLastPInvokeError();
                        if (error == PtyNative.EAgain)
                            PtyNative.Wait(fd, 4, 50); // POLLOUT; bounded so cancellation ends this worker.
                        else if (error != PtyNative.EIntr)
                            throw PtyNative.Failed("writing to the terminal");
                    }
                    else
                        left = left[(int)wrote..];
                }
            }), CancellationToken.None);
        }
        finally
        {
            _writing.Release();
        }
    }

    public void Resize(int cols, int rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Using(fd => PtyNative.SetSize(fd, cols, rows));
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;
        }
        // The shell's group and what it started, then every process left in its session: an interactive shell gives
        // each job a group of its own, and a job the shell left when it exited has moved under init. Not once it has
        // ended and nothing holds the terminal: its pid may belong to someone else's group or session by now.
        if (!(_ended && _process.HasExited))
        {
            ProcessGroup.Kill(Pid);
            foreach (var pid in InSession(Pid))
                _ = PtyNative.kill(pid, 9);
        }
        CloseSlave();
        _output.Writer.TryComplete();
        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<int> WaitAsync()
    {
        await _process.WaitForExitAsync();
        var code = _process.ExitCode;
        // The shell's end is the terminal's end, as when a terminal app's window closes: what it left running in its
        // session is hung up, and with the backend's slave closed the master reads end of file. A job that ignores the
        // hangup and still holds the terminal (nohup lets go of it) keeps the output open until it ends or is closed.
        foreach (var pid in InSession(Pid))
            _ = PtyNative.kill(pid, 1);
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

    private static List<int> InSession(int session)
    {
        var start = new ProcessStartInfo("/bin/ps", ["-A", "-o", "pid="]) { RedirectStandardOutput = true, UseShellExecute = false };
        using var ps = Process.Start(start)!;
        var found = new List<int>();
        while (ps.StandardOutput.ReadLine() is { } line)
            if (int.TryParse(line, System.Globalization.CultureInfo.InvariantCulture, out var pid) && PtyNative.getsid(pid) == session)
                found.Add(pid);
        ps.WaitForExit();
        return found;
    }

    // Runs with a reference on the master, so its number can't be closed and handed to another file meanwhile;
    // throws ObjectDisposedException once the master has closed.
    private void Using(Action<int> action)
    {
        var added = false;
        try
        {
            _master.DangerousAddRef(ref added);
            action((int)_master.DangerousGetHandle());
        }
        finally
        {
            if (added)
                _master.DangerousRelease();
        }
    }

    private void ReadMaster()
    {
        var buffer = new byte[ReadSize];
        var added = false;
        _master.DangerousAddRef(ref added);
        var fd = (int)_master.DangerousGetHandle();
        try
        {
            while (true)
            {
                var read = PtyNative.read(fd, ref buffer[0], buffer.Length);
                if (read < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == PtyNative.EAgain)
                    {
                        PtyNative.Wait(fd, 1, -1); // POLLIN; slave closure wakes the reader too.
                        continue;
                    }
                    if (error == PtyNative.EIntr)
                        continue;
                }
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
            _ended = true;
            _output.Writer.TryComplete();
            _master.DangerousRelease();
            _master.Dispose();
        }
    }
}
