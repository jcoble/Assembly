namespace Assembly.Infrastructure.Terminal;

/// <summary>
/// The libc calls the PTY needs, the same on macOS and Linux: posix_openpt, grantpt, unlockpt, ptsname, fcntl
/// (close-on-exec), ioctl (TIOCSWINSZ), kill. LibraryImport, so they work under Native AOT.
/// </summary>
internal static partial class PtyNative
{
}
