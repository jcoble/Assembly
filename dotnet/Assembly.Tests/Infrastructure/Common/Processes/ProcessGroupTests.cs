using System.Diagnostics;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Common.Processes;

namespace Assembly.Tests.Infrastructure.Common.Processes;

public sealed class ProcessGroupTests
{
    [Fact]
    public void A_ps_row_of_three_pids_is_read()
    {
        Assert.Equal((4089422, 1, 4089422), ProcessGroup.ParseRow("  4089422       1 4089422"));
    }

    [Theory]
    [InlineData("1 1 4294967295")]
    [InlineData("1 -1 1")]
    [InlineData("1 1 -")]
    [InlineData("1 1")]
    [InlineData("1 1 1 1")]
    [InlineData("")]
    public void A_row_that_is_not_three_pids_is_skipped_so_the_kill_still_runs(string row)
    {
        Assert.Null(ProcessGroup.ParseRow(row));
    }

    private const string Writer = "while :; do printf .; done";

    private static Process Start(AgentCommand command)
    {
        var start = new ProcessStartInfo(command.FileName, command.Arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        return Process.Start(start)!;
    }

    public static TheoryData<string> Starts => new() { "leading", "supervised" };

    private static AgentCommand Started(string how) => how == "leading"
        ? ProcessGroup.Leading(new("/bin/sh", ["-c", Writer], "/", null))
        : ProcessGroup.Supervised(new("/bin/sh", ["-c", Writer], "/", null));

    [Theory]
    [MemberData(nameof(Starts))]
    public async Task A_writer_whose_reader_is_gone_ends_instead_of_spinning(string how)
    {
        using var process = Start(Started(how));
        try
        {
            await process.StandardOutput.BaseStream.ReadExactlyAsync(new byte[1]);
            process.StandardOutput.Close();
            process.StandardError.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(141, process.ExitCode);
        }
        finally { ProcessGroup.Kill(process.Id); }
    }

    [Fact]
    public async Task A_supervised_command_ends_with_its_parent_even_when_the_parent_is_killed()
    {
        // A stand-in for the backend: a parent we can really kill -9, which starts the command and says its pid and its group's.
        var command = ProcessGroup.Supervised(new("/bin/sh", ["-c", "echo $$; exec sleep 600"], "/", null));
        var quoted = string.Join(' ', command.Arguments.Select(argument => "'" + argument.Replace("'", "'\\''") + "'"));
        using var parent = Start(new("/bin/sh", ["-c", $"{command.FileName} {quoted} & echo $!; wait"], "/", null));
        var leader = int.Parse((await parent.StandardOutput.ReadLineAsync())!);
        var sleeper = int.Parse((await parent.StandardOutput.ReadLineAsync())!);
        try
        {
            Assert.True(Alive(leader) && Alive(sleeper));
            parent.Kill();
            await parent.WaitForExitAsync();
            await GoneAsync(sleeper);
            await GoneAsync(leader);
        }
        finally { ProcessGroup.Kill(leader); }
    }

    [Theory]
    [InlineData("exit 3", 3)]
    [InlineData("kill -KILL $$", 137)]
    public async Task A_supervised_command_exits_as_its_command_did(string script, int code)
    {
        using var process = Start(ProcessGroup.Supervised(new("/bin/sh", ["-c", script], "/", null)));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(code, process.ExitCode);
    }

    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task GoneAsync(int pid)
    {
        var limit = Stopwatch.StartNew();
        while (Alive(pid))
        {
            Assert.True(limit.Elapsed < TimeSpan.FromSeconds(10), $"pid {pid} outlived its parent");
            await Task.Delay(50);
        }
    }
}
