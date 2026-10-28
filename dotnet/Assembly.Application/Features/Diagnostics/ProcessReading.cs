namespace Assembly.Application.Features.Diagnostics;

/// <summary>
/// One process in the backend's tree: its pid, the CPU time it has used so far, its resident memory, and when it
/// started, so a pid used again by a new process isn't taken for the old one.
/// </summary>
public sealed record ProcessReading(int Pid, TimeSpan Cpu, long MemoryBytes, long Start);
