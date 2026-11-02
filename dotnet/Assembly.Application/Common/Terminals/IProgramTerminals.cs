namespace Assembly.Application.Common.Terminals;

/// <summary>A direct program on its own terminal, with no shell or session-folder lookup.</summary>
public sealed record ProgramStart(string File, IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string?> Env, string Cwd, string Owner,
    int Cols = 96, int Rows = 28);

public sealed record ProgramTerminal(string Id, int Pid);

public interface IProgramTerminals
{
    Task<ProgramTerminal> StartProgramAsync(ProgramStart start, CancellationToken ct);
    IAsyncEnumerable<ReadOnlyMemory<byte>> OutputAsync(string terminal, CancellationToken ct);
    Task<int> ExitedAsync(string terminal, CancellationToken ct);
    ValueTask WriteAsync(string terminal, ReadOnlyMemory<byte> data, CancellationToken ct);
    ValueTask CloseAsync(string terminal, CancellationToken ct);
}
