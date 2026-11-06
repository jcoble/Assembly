using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

using Assembly.Application.Common.Agents;
namespace Assembly.Infrastructure.Common.Processes;

/// <summary>
/// Starts the adapter as the leader of its own process group, so killing the group also ends Claude Code, even
/// once the adapter itself is gone. .NET's Process can't set a group, so the command goes through perl (at
/// /usr/bin/perl on macOS and Ubuntu, nothing to install), which calls setpgrp and then execs the adapter in place:
/// the pid stays the same, so the adapter's pid is the group's id. Both starts put SIGPIPE back to its default: .NET
/// ignores it, an ignored signal stays ignored through exec and a shell can't undo that, so a shell loop that writes to
/// a pipe nobody reads would otherwise spin at 100% of a core forever on EPIPE.
/// </summary>
internal static partial class ProcessGroup
{
    private const int SigKill = 9;

    public static AgentCommand Leading(AgentCommand command) =>
        command with
        {
            FileName = "/usr/bin/perl",
            Arguments = ["-e", "setpgrp(0, 0); $SIG{PIPE} = 'DEFAULT'; exec { $ARGV[0] } @ARGV or die \"exec $ARGV[0]: $!\\n\"", "--", command.FileName, .. command.Arguments],
        };

    /// <summary>
    /// For a command nothing else would stop when this process dies, even by kill -9. Perl stays as the group's leader,
    /// runs the command as its child and exits as the command did; once its parent is gone, it kills the group.
    /// SIGCHLD wakes the sleep, so the command's exit is seen at once.
    /// </summary>
    public static AgentCommand Supervised(AgentCommand command) =>
        command with
        {
            FileName = "/usr/bin/perl",
            Arguments = ["-e", Supervisor, "--", command.FileName, .. command.Arguments],
        };

    private const string Supervisor =
        """use POSIX ":sys_wait_h"; setpgrp(0, 0); $SIG{PIPE} = 'DEFAULT'; my $parent = getppid(); $SIG{CHLD} = sub {}; """
        + """defined(my $child = fork) or die "fork: $!\n"; if (!$child) { exec { $ARGV[0] } @ARGV or die "exec $ARGV[0]: $!\n" } """
        + """while (waitpid($child, WNOHANG) == 0) { kill "KILL", -$$ if getppid() != $parent; sleep 1 } """
        + """exit($? & 127 ? 128 + ($? & 127) : $? >> 8);""";

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

    /// <summary>Everything the process started that is still attached to it, found by parent pid. Never by name.</summary>
    public static List<int> Descendants(int pid)
    {
        var processes = Snapshot();
        var found = new HashSet<int> { pid };
        Grow(processes, found);
        found.Remove(pid);
        return [.. found];
    }

    /// <summary>Whether the pid is still listed, a killed process not yet cleared by its parent included.</summary>
    public static bool Listed(int pid) => kill(pid, 0) == 0;

    private static HashSet<int> DescendantGroups(int groupId)
    {
        var processes = Snapshot();
        var members = processes.Where(process => process.Group == groupId).Select(process => process.Pid).ToHashSet();
        var found = members.ToHashSet();
        Grow(processes, found);
        return processes.Where(process => found.Contains(process.Pid) && !members.Contains(process.Pid) && process.Group != groupId)
            .Select(process => process.Group).ToHashSet();
    }

    private static void Grow(List<(int Pid, int Parent, int Group)> processes, HashSet<int> found)
    {
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var process in processes)
                grew |= found.Contains(process.Parent) && found.Add(process.Pid);
        }
    }

    private static List<(int Pid, int Parent, int Group)> Snapshot()
    {
        var start = new ProcessStartInfo("/bin/ps", ["-A", "-o", "pid=,ppid=,pgid="]) { RedirectStandardOutput = true, UseShellExecute = false };
        using var ps = Process.Start(start)!;
        var processes = new List<(int, int, int)>();
        while (ps.StandardOutput.ReadLine() is { } line)
        {
            if (ParseRow(line) is { } row)
                processes.Add(row);
        }

        ps.WaitForExit();
        return processes;
    }

    /// <summary>One `ps` row. A row that isn't three ids that fit a pid is not a process this can signal, so it is skipped rather than failing the kill.</summary>
    internal static (int Pid, int Parent, int Group)? ParseRow(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 3
            && int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)
            && int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var group))
            return (pid, parent, group);
        return null;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
