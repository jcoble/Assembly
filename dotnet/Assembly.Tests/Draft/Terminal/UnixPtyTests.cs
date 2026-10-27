using System.Diagnostics;
using System.Text;
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
    public async Task Ctrl_c_stops_the_program_because_the_terminal_is_its_own()
    {
        // Pins: terminal#2
        await using var pty = Start("echo ready; sleep 300");
        await ReadUntilAsync(pty, "ready");

        await pty.WriteAsync(new byte[] { 0x03 }, Cancel());
        var exit = await pty.Exited.WaitAsync(Cancel());

        Assert.True(exit.Signal == 2 || exit.Code == 130, $"ended with {exit}");
    }

    [Fact]
    public async Task The_program_sees_its_size_and_a_resize()
    {
        // Pins: terminal#4
        await using var pty = Start("stty size; echo ready; read x; stty size", cols: 80, rows: 24);
        await ReadUntilAsync(pty, "24 80");
        await ReadUntilAsync(pty, "ready");

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
        Assert.Equal(new PtyExit(7, null), exit);
    }

    [Fact]
    public async Task Disposing_ends_the_program_and_everything_it_started()
    {
        // Pins: terminal#8
        var pty = Start("sleep 300 & echo \"child:$!\"; wait");
        var line = await ReadUntilAsync(pty, "child:");
        var child = int.Parse(line[(line.IndexOf("child:", StringComparison.Ordinal) + 6)..].Split('\r', '\n')[0]);

        await pty.DisposeAsync();

        await GoneAsync(child);
        await GoneAsync(pty.Pid);
    }

    private static IPty Start(string script, int cols = 80, int rows = 24) =>
        new UnixPtyStarter().Start(new PtyRequest("/bin/sh", ["-c", script], Path.GetTempPath(), NoEnvironment, cols, rows));

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
