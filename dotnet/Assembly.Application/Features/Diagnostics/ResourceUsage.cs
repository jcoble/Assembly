namespace Assembly.Application.Features.Diagnostics;

/// <summary>
/// What the <c>resources</c> topic carries for the backend and every process under it: the percentage of one core
/// used over the last window (null until a window has closed) and the resident memory in bytes.
/// </summary>
internal sealed record ResourceUsage(double? CpuPercent, long MemoryBytes);
