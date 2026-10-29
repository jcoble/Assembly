using System.Runtime.InteropServices;

namespace Assembly.Infrastructure.Terminal;

/// <summary>
/// The libc calls the PTY needs, with each platform's numbers (Linux's from asm-generic, macOS's from sys/ttycom.h and
/// sys/fcntl.h). DllImport with blittable arguments, as Agents/ProcessGroup.cs does, so they work under Native AOT.
/// </summary>
internal static class PtyNative
{
    public const int EIntr = 4;
    public static int EAgain => Mac ? 35 : 11;

    private static readonly bool Mac = OperatingSystem.IsMacOS();
    private static readonly Lock PtsnameLock = new();

    public static int OpenFlags => 2 | (Mac ? 0x20000 | 0x1000000 : 0x100 | 0x80000); // O_RDWR | O_NOCTTY | O_CLOEXEC

    public static nuint TiocSctty => Mac ? 0x20007461u : 0x540Eu;

    private static nuint TiocSwinsz => Mac ? 0x80087467u : 0x5414u;

    /// <summary>A new master, close-on-exec, and the path of its slave.</summary>
    public static (int Master, string Slave) OpenMaster()
    {
        // macOS's posix_openpt takes no O_CLOEXEC, so FIOCLEX sets it after.
        var master = posix_openpt(Mac ? 2 | 0x20000 : OpenFlags);
        if (master < 0)
            throw Failed("posix_openpt");
        if ((Mac && ioctl(master, 0x20006601u) != 0) || grantpt(master) != 0 || unlockpt(master) != 0)
        {
            var error = Failed("setting up the pty");
            _ = close(master);
            throw error;
        }
        var enabled = 1;
        var nonblocking = Mac && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? ioctl(master, 0x8004667eu, 0, 0, 0, 0, 0, 0, ref enabled)
            : ioctl(master, Mac ? 0x8004667eu : 0x5421u, ref enabled);
        if (nonblocking != 0)
        {
            var error = Failed("making the pty nonblocking");
            _ = close(master);
            throw error;
        }
        // ptsname answers a static buffer, so its result is copied before another call can overwrite it.
        lock (PtsnameLock)
            return (master, Marshal.PtrToStringUTF8(ptsname(master)) ?? throw Failed("ptsname"));
    }

    public static void SetSize(int fd, int cols, int rows)
    {
        var size = new WinSize((ushort)rows, (ushort)cols, 0, 0);
        // ioctl is variadic, and on Apple arm64 a variadic argument goes on the stack, not in a register: six unused
        // register arguments push the size there.
        var done = Mac && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? ioctl(fd, TiocSwinsz, 0, 0, 0, 0, 0, 0, ref size)
            : ioctl(fd, TiocSwinsz, ref size);
        if (done != 0)
            throw Failed("resizing the pty");
    }

    public static void Wait(int fd, short events, int milliseconds)
    {
        var descriptor = new PollFd { Fd = fd, Events = events };
        if (poll(ref descriptor, 1, milliseconds) < 0 && Marshal.GetLastPInvokeError() != EIntr)
            throw Failed("waiting for the terminal");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Returned;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref PollFd descriptor, nuint count, int milliseconds);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, ref int enabled);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, ref int enabled);

    public static IOException Failed(string what) => new($"{what} failed: {Marshal.GetLastPInvokeErrorMessage()}");

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct WinSize(ushort Rows, ushort Cols, ushort XPixels, ushort YPixels);

    [DllImport("libc", SetLastError = true)]
    public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    public static extern nint read(int fd, ref byte buffer, nint count);

    [DllImport("libc", SetLastError = true)]
    public static extern nint write(int fd, ref byte buffer, nint count);

    [DllImport("libc", SetLastError = true)]
    public static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    public static extern int getsid(int pid);

    [DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int signal);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_openpt(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int grantpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlockpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern nint ptsname(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, ref WinSize size);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, ref WinSize size);
}
