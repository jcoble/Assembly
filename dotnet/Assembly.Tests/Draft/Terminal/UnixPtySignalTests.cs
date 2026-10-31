using System.Runtime.InteropServices;
using System.Text;
using Assembly.Application.Features.Terminal;
using Assembly.Infrastructure.Terminal;

namespace Assembly.Tests.Draft.Terminal;

public sealed class UnixPtySignalTests
{
    private static readonly Lock SignalLock = new();

    [Fact]
    public async Task Child_exec_resets_ignored_interrupt_and_quit()
    {
        await using var pty = StartWithIgnoredSignals("/usr/bin/perl",
            ["-e", "print(($SIG{INT} || 'DEFAULT').':'.($SIG{QUIT} || 'DEFAULT'))"]);
        Assert.Equal("DEFAULT:DEFAULT", await ReadUntilAsync(pty, ":DEFAULT"));
        Assert.Equal(0, await pty.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Ctrl_c_interrupts_a_job_and_keeps_the_shell_after_an_ignored_parent()
    {
        await using var pty = StartWithIgnoredSignals("/bin/bash", ["--noprofile", "--norc", "-i"]);
        await pty.WriteAsync("PS1='ASSEMBLY_PROMPT> '; stty -echo; printf '\\nREADY\\n'\n"u8.ToArray(), CancellationToken.None);
        await ReadUntilAsync(pty, "\r\nREADY\r\n");
        await pty.WriteAsync("sh -c 'echo SLEEPING; exec sleep 300'\n"u8.ToArray(), CancellationToken.None);
        await ReadUntilAsync(pty, "SLEEPING");
        await pty.WriteAsync(new byte[] { 3 }, CancellationToken.None);
        await ReadUntilAsync(pty, "ASSEMBLY_PROMPT> ");
        await pty.WriteAsync("printf '\\nINTERRUPTED:%s\\n' \"$?\"\n"u8.ToArray(), CancellationToken.None);
        await ReadUntilAsync(pty, "INTERRUPTED:130");
        Assert.False(pty.Exited.IsCompleted);
        await pty.WriteAsync("echo STILL_ALIVE; exit 0\n"u8.ToArray(), CancellationToken.None);
        await ReadUntilAsync(pty, "STILL_ALIVE");
        Assert.Equal(0, await pty.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static IPty StartWithIgnoredSignals(string program, string[] arguments)
    {
        // Ignore only across the synchronous spawn, then restore the test host immediately (including on failure).
        lock (SignalLock)
        {
            var interrupt = signal(2, 1);
            var quit = signal(3, 1);
            try
            {
                return new UnixPtyStarter().Start(new PtyRequest(program, arguments, Path.GetTempPath(), new Dictionary<string, string?>(), 80, 24));
            }
            finally
            {
                _ = signal(2, interrupt);
                _ = signal(3, quit);
            }
        }
    }

    private static async Task<string> ReadUntilAsync(IPty pty, string expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var seen = new StringBuilder();
        var bytes = new byte[4096];
        while (!seen.ToString().Contains(expected, StringComparison.Ordinal))
        {
            var read = await pty.ReadAsync(bytes, deadline.Token);
            Assert.True(read > 0, $"terminal ended before {expected}; output: {seen}");
            seen.Append(Encoding.UTF8.GetString(bytes, 0, read));
        }
        return seen.ToString();
    }

    [DllImport("libc")]
    private static extern nint signal(int number, nint handler);
}
