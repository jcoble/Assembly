namespace Assembly.Application.Features.Diagnostics;

/// <summary>One process in the backend's tree: its pid, the CPU time it has used so far, and its resident memory.</summary>
public sealed record ProcessReading(int Pid, TimeSpan Cpu, long MemoryBytes);
