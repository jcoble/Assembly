namespace Assembly.Application.Features.Terminal;

/// <summary>The terminal feature's operations, on the machine that owns the shell.</summary>
internal interface ITerminals
{
    Task<TerminalInfo> StartAsync(StartArgs args, CancellationToken cancellation = default);
    Task<IReadOnlyList<TerminalInfo>> ListAsync(string? owner, CancellationToken cancellation);
    ValueTask WriteAsync(string terminal, byte[] data, CancellationToken cancellation);
    Task ResizeAsync(string terminal, int cols, int rows, CancellationToken cancellation);
    Task<OutputPiece> ReadAsync(string terminal, long offset, CancellationToken cancellation);
    ValueTask CloseAsync(string terminal, CancellationToken cancellation = default);
}

internal sealed record ListArgs(string? Owner = null);
