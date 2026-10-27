using System.Diagnostics;
using System.Globalization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Terminal;

namespace Assembly.Infrastructure.Terminal;

/// <summary>
/// Opens a PTY master with posix_openpt and starts the process through /usr/bin/perl, as Agents/ProcessGroup.cs does:
/// perl calls setsid, opens the slave, makes it the controlling terminal (TIOCSCTTY), puts it on fds 0 to 2 and execs
/// the program in place, so the pid stays the same and the program leads its own session and group. Forking from
/// managed code isn't safe, and posix_spawn can't set a controlling terminal on macOS.
/// </summary>
internal sealed class UnixPtyStarter : IPtyStarter
{
    private const string Child =
        "use POSIX (); my ($tty, $ctty) = splice @ARGV, 0, 2; POSIX::setsid() or die \"setsid: $!\\n\";"
        + " open(my $t, '+<', $tty) or die \"open $tty: $!\\n\"; defined ioctl($t, $ctty, 0) or die \"TIOCSCTTY: $!\\n\";"
        + " POSIX::dup2(fileno $t, $_) for 0 .. 2; close $t if fileno($t) > 2; exec { $ARGV[0] } @ARGV or die \"exec $ARGV[0]: $!\\n\"";

    public IPty Start(PtyRequest request)
    {
        if (!Directory.Exists(request.Cwd))
            throw new CallFailed(File.Exists(request.Cwd)
                ? $"This session's folder isn't a folder: {request.Cwd}"
                : $"This session's folder no longer exists: {request.Cwd}");
        var (master, slavePath) = PtyNative.OpenMaster();
        var slave = -1;
        try
        {
            PtyNative.SetSize(master, request.Cols, request.Rows);
            // The backend holds the slave open too until the process ends, so the master can't read end of file before
            // perl has opened it.
            slave = PtyNative.open(slavePath, PtyNative.OpenFlags);
            if (slave < 0)
                throw PtyNative.Failed($"opening {slavePath}");
            var start = new ProcessStartInfo("/usr/bin/perl") { UseShellExecute = false, WorkingDirectory = request.Cwd };
            foreach (var argument in (string[])["-e", Child, "--", slavePath, PtyNative.TiocSctty.ToString(CultureInfo.InvariantCulture), request.FileName, .. request.Arguments])
                start.ArgumentList.Add(argument);
            foreach (var (name, value) in request.Environment)
                start.Environment[name] = value;
            var process = Process.Start(start) ?? throw new IOException("perl didn't start");
            return new UnixPty(master, slave, process);
        }
        catch
        {
            if (slave >= 0)
                _ = PtyNative.close(slave);
            _ = PtyNative.close(master);
            throw;
        }
    }
}
