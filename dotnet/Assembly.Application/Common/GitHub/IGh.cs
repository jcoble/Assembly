namespace Assembly.Application.Common.GitHub;

public interface IGh
{
    Task<GhRun> RunAsync(string folder, IReadOnlyList<string> arguments, string? stdinJson, TimeSpan limit, CancellationToken ct);
}
public sealed record GhRun(int ExitCode, string Output, string Error)
{
    public ReadOnlyMemory<byte>? OutputBytes { get; init; }
}
