using System.Diagnostics;
using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Terminal;
using Assembly.Infrastructure.Terminal;

namespace Assembly.Tests.Draft.Terminal;

/// <summary>P22's dependency proof: a real PTY on this machine (agent-workbox, Workbox Test, the Mac).</summary>
public sealed class UnixPtyTests
{
    private static readonly Dictionary<string, string?> NoEnvironment = [];

    [Fact]
    public async Task Output_comes_out_byte_for_byte()
    {
        // Pins: terminal#3
        await using var pty = Start("printf '\\344\\270'; printf '\\226\\377'");

        var output = await ReadToEndAsync(pty);

        Assert.Equal(new byte[] { 0xe4, 0xb8, 0x96, 0xff }, output);
    }

    [Fact]
    public async Task What_you_type_reaches_the_program()
    {
        // Pins: terminal#2
        await using var pty = Start("echo ready; read line; echo \"got:$line\"");
        await ReadUntilAsync(pty, "ready");

        await pty.WriteAsync("hi\n"u8.ToArray(), Cancel());

        await ReadUntilAsync(pty, "got:hi");
    }

    [Fact]
    public async Task A_write_cancelled_while_the_program_isn_t_reading_stops_and_the_next_comes_after_it()
    {
        // Pins: terminal#2
        await using var pty = Start("stty -echo -icanon; echo ready; sleep 2; perl -e '$SIG{ALRM} = sub { exit }; alarm 2; print $b while sysread STDIN, $b, 65536'; echo; echo end");
        await ReadUntilAsync(pty, "ready");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pty.WriteAsync(Encoding.ASCII.GetBytes(new string('a', 1024 * 1024)), new CancellationTokenSource(300).Token).AsTask());
        await pty.WriteAsync(Encoding.ASCII.GetBytes(new string('b', 64)), Cancel());

        var typed = (await ReadUntilAsync(pty, "end")).Split("end")[0].Trim();
        Assert.Matches("^a*b{64}$", typed);
        Assert.True(typed.Length < 64 * 1024, $"the cancelled write stopped, but {typed.Length} bytes came");
    }

    [Fact]
    public async Task Ctrl_c_stops_the_program_because_the_terminal_is_its_own()
    {
        // Pins: terminal#2
        await using var pty = Start("echo ready; sleep 300");
        await ReadUntilAsync(pty, "ready");

        await pty.WriteAsync(new byte[] { 0x03 }, Cancel());
        var exit = await pty.Exited.WaitAsync(Cancel());

        Assert.Equal(130, exit);
    }

    [Fact]
    public async Task The_program_sees_its_size_and_a_resize()
    {
        // Pins: terminal#4
        await using var pty = Start("stty size; echo ready; read x; stty size", cols: 80, rows: 24);
        Assert.Contains("24 80", await ReadUntilAsync(pty, "ready"));

        pty.Resize(120, 40);
        await pty.WriteAsync("\n"u8.ToArray(), Cancel());

        await ReadUntilAsync(pty, "40 120");
    }

    [Fact]
    public async Task The_exit_code_comes_after_all_the_output()
    {
        // Pins: terminal#7
        await using var pty = Start("echo done; exit 7");

        var output = Encoding.UTF8.GetString(await ReadToEndAsync(pty));
        var exit = await pty.Exited.WaitAsync(Cancel());

        Assert.Contains("done", output);
        Assert.Equal(7, exit);
    }

    [Fact]
    public async Task Disposing_ends_the_program_and_everything_it_started()
    {
        // Pins: terminal#8
        // An interactive shell puts each job in a process group of its own, which killing the shell's group alone misses.
        var pty = Start("sleep 300 & echo \"child:$!\"; wait", interactive: true);
        var line = await ReadUntilAsync(pty, "child:");
        var child = int.Parse(line[(line.IndexOf("child:", StringComparison.Ordinal) + 6)..].Split('\r', '\n')[0]);

        await pty.DisposeAsync();

        await GoneAsync(child);
        await GoneAsync(pty.Pid);
    }

    [Fact]
    public void A_folder_that_no_longer_exists_fails_with_its_path()
    {
        // Pins: terminal#1
        var gone = Path.Combine(Path.GetTempPath(), $"assembly-gone-{Guid.NewGuid():N}");

        var failed = Assert.Throws<CallFailed>(() => new UnixPtyStarter().Start(new PtyRequest("/bin/sh", [], gone, NoEnvironment, 80, 24)));

        Assert.Contains(gone, failed.Message);
    }

    [Fact]
    public async Task Closing_after_the_shell_has_exited_ends_the_jobs_it_left()
    {
        // Pins: terminal#8
        // The job outlives the shell in a group of its own, under init, and still holds the terminal.
        var pty = Start("sleep 300 & echo \"child:$!\"", interactive: true);
        var line = await ReadUntilAsync(pty, "child:");
        var child = int.Parse(line[(line.IndexOf("child:", StringComparison.Ordinal) + 6)..].Split('\r', '\n')[0]);
        await pty.Exited.WaitAsync(Cancel());

        await pty.DisposeAsync();

        await GoneAsync(child);
    }

    [Fact]
    public async Task The_output_ends_when_the_shell_exits_even_with_a_job_it_left_running()
    {
        // Pins: terminal#7
        await using var pty = Start("sleep 300 & echo \"child:$!\"", interactive: true);
        var line = await ReadUntilAsync(pty, "child:");
        var child = int.Parse(line[(line.IndexOf("child:", StringComparison.Ordinal) + 6)..].Split('\r', '\n')[0]);

        await ReadToEndAsync(pty);

        await GoneAsync(child);
    }

    private static IPty Start(string script, int cols = 80, int rows = 24, bool interactive = false) =>
        new UnixPtyStarter().Start(new PtyRequest("/bin/sh", interactive ? ["-i", "-c", script] : ["-c", script], Path.GetTempPath(),
            NoEnvironment, cols, rows));

    private static CancellationToken Cancel() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static async Task<byte[]> ReadToEndAsync(IPty pty)
    {
        var all = new MemoryStream();
        var buffer = new byte[4096];
        var cancel = Cancel();
        int read;
        while ((read = await pty.ReadAsync(buffer, cancel)) > 0)
            all.Write(buffer, 0, read);
        return all.ToArray();
    }

    // Reads until the text has come out, and answers everything read so far.
    private static async Task<string> ReadUntilAsync(IPty pty, string text)
    {
        var seen = new StringBuilder();
        var buffer = new byte[4096];
        var cancel = Cancel();
        while (!seen.ToString().Contains(text, StringComparison.Ordinal))
        {
            var read = await pty.ReadAsync(buffer, cancel);
            Assert.True(read > 0, $"the terminal closed before \"{text}\"; it wrote: {seen}");
            seen.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
        return seen.ToString();
    }

    // As AgentProcessTests waits: a killed process can stay a zombie until its parent or init reaps it.
    private static async Task GoneAsync(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Running(pid))
        {
            Assert.True(DateTime.UtcNow < deadline, $"pid {pid} outlived its terminal");
            await Task.Delay(20);
        }
    }

    private static bool Running(int pid)
    {
        using var ps = Process.Start(new ProcessStartInfo("ps", ["-o", "stat=", "-p", pid.ToString()]) { RedirectStandardOutput = true })!;
        var stat = ps.StandardOutput.ReadToEnd().Trim();
        ps.WaitForExit();
        return stat.Length > 0 && !stat.StartsWith('Z');
    }
}
