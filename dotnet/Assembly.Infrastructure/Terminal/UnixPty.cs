using Assembly.Application.Features.Terminal;

namespace Assembly.Infrastructure.Terminal;

/// <summary>The master side of a started PTY, and the process on it, which leads its own process group.</summary>
internal sealed class UnixPty : IPty
{
    public int Pid => throw new NotImplementedException();

    public Task<PtyExit> Exited => throw new NotImplementedException();

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => throw new NotImplementedException();

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => throw new NotImplementedException();

    public void Resize(int cols, int rows) => throw new NotImplementedException();

    public ValueTask DisposeAsync() => throw new NotImplementedException();
}
