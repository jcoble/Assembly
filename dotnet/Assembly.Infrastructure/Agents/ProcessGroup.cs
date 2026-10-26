using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

using Assembly.Application.Common.Agents;
namespace Assembly.Infrastructure.Agents;

/// <summary>
/// Starts the adapter as the leader of its own process group, so killing the group also ends Claude Code, even
/// once the adapter itself is gone. .NET's Process can't set a group, so the command goes through perl (at
/// /usr/bin/perl on macOS and Ubuntu, nothing to install), which calls setpgrp and then execs the adapter in place:
/// the pid stays the same, so the adapter's pid is the group's id.
/// </summary>
internal static partial class ProcessGroup
{
    private const int SigKill = 9;

    public static AgentCommand Leading(AgentCommand command) =>
        command with
        {
            FileName = "/usr/bin/perl",
            Arguments = ["-e", "setpgrp(0, 0); exec { $ARGV[0] } @ARGV or die \"exec $ARGV[0]: $!\\n\"", "--", command.FileName, .. command.Arguments],
        };

    /// <summary>
    /// SIGKILLs the group, and first the groups of everything the group's members started. Claude Code starts its
    /// Bash tool's shell with setsid, outside the group, so the group alone would leave that shell running. Only
    /// processes this backend's adapter started, found by parent pid while they're still attached. Never by name.
    /// </summary>
    public static void Kill(int groupId)
    {
        foreach (var other in DescendantGroups(groupId))
            _ = kill(-other, SigKill);
        _ = kill(-groupId, SigKill);
    }

    private static HashSet<int> DescendantGroups(int groupId)
    {
        var processes = Snapshot();
        var found = processes.Where(process => process.Group == groupId).Select(process => process.Pid).ToHashSet();
        var groups = new HashSet<int>();
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var process in processes)
            {
                if (found.Contains(process.Parent) && found.Add(process.Pid))
                {
                    grew = true;
                    if (process.Group != groupId)
                        groups.Add(process.Group);
                }
            }
        }

        return groups;
    }

    private static List<(int Pid, int Parent, int Group)> Snapshot()
    {
        var start = new ProcessStartInfo("/bin/ps", ["-A", "-o", "pid=,ppid=,pgid="]) { RedirectStandardOutput = true, UseShellExecute = false };
        using var ps = Process.Start(start)!;
        var processes = new List<(int, int, int)>();
        while (ps.StandardOutput.ReadLine() is { } line)
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 3)
                processes.Add((Parse(fields[0]), Parse(fields[1]), Parse(fields[2])));
        }

        ps.WaitForExit();
        return processes;
    }

    private static int Parse(string field) => int.Parse(field, CultureInfo.InvariantCulture);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
