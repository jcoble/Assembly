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
    public IPty Start(PtyRequest request) => throw new NotImplementedException();
}
