using System.Diagnostics;
using System.Globalization;
using Assembly.Application.Features.Diagnostics;

namespace Assembly.Infrastructure.Diagnostics;

/// <summary>
/// The backend and every process under it (its adapters, agents and CLIs), each with its CPU time and resident memory.
/// On Linux it reads /proc, with no process started per sample. On macOS and Windows it reads only the backend itself,
/// from <see cref="Process"/>: walking the tree there takes libproc (macOS) or a process snapshot (Windows), not done yet.
/// </summary>
internal static class ProcessTree
{
    // /proc counts CPU time in USER_HZ ticks, which the kernel fixes at 100 a second on every architecture .NET runs on.
    private const double TicksPerSecond = 100;

    public static IReadOnlyList<ProcessReading> Read()
    {
        if (!OperatingSystem.IsLinux())
        {
            using var self = Process.GetCurrentProcess();
            return [new ProcessReading(self.Id, self.TotalProcessorTime, self.WorkingSet64)];
        }
        var stats = new Dictionary<int, (int Parent, TimeSpan Cpu)>();
        foreach (var folder in Directory.EnumerateDirectories("/proc"))
            if (int.TryParse(Path.GetFileName(folder), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && Stat(pid) is { } stat)
                stats[pid] = stat;
        var children = stats.ToLookup(pair => pair.Value.Parent, pair => pair.Key);
        var tree = new List<ProcessReading>();
        var next = new Queue<int>([Environment.ProcessId]);
        while (next.TryDequeue(out var pid))
        {
            // A process that exited since the scan is left out.
            if (Resident(pid) is { } memory)
                tree.Add(new ProcessReading(pid, stats[pid].Cpu, memory));
            foreach (var child in children[pid])
                next.Enqueue(child);
        }
        return tree;
    }

    // The parent and the user plus system CPU time. The name in brackets may hold spaces, so fields count from the last
    // ')': the state, then the parent, and user and system time 11 and 12 after the state.
    private static (int Parent, TimeSpan Cpu)? Stat(int pid)
    {
        if (ReadOrNull($"/proc/{pid}/stat") is not { } text)
            return null;
        var fields = text[(text.LastIndexOf(')') + 2)..].Split(' ');
        var ticks = long.Parse(fields[11], CultureInfo.InvariantCulture) + long.Parse(fields[12], CultureInfo.InvariantCulture);
        return (int.Parse(fields[1], CultureInfo.InvariantCulture), TimeSpan.FromSeconds(ticks / TicksPerSecond));
    }

    // statm's second field is the resident size in pages.
    private static long? Resident(int pid) =>
        ReadOrNull($"/proc/{pid}/statm") is { } text
            ? long.Parse(text.Split(' ')[1], CultureInfo.InvariantCulture) * Environment.SystemPageSize
            : null;

    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
