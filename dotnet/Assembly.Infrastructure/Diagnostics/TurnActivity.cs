using System.Runtime.InteropServices;

namespace Assembly.Infrastructure.Diagnostics;

/// <summary>
/// While any turn runs, an <c>NSProcessInfo</c> activity keeps App Nap from throttling the app. A user-initiated
/// activity that still allows idle system sleep, so the Mac and its display may still sleep. Holding it while it's
/// held does nothing. Off macOS only <see cref="Held"/> changes.
/// </summary>
internal sealed class TurnActivity
{
    // NSActivityUserInitiatedAllowingIdleSystemSleep.
    private const ulong UserInitiatedAllowingIdleSystemSleep = 0x00EFFFFF;

    private readonly Lock _lock = new();
    private IntPtr _token;
    private bool _held;

    public bool Held
    {
        get { lock (_lock) return _held; }
    }

    /// <summary>Holds it while <paramref name="held"/>, else releases it.</summary>
    public void Hold(bool held)
    {
        lock (_lock)
        {
            if (held == _held)
                return;
            if (OperatingSystem.IsMacOS() && held)
                Begin();
            else if (OperatingSystem.IsMacOS())
                End();
            _held = held;
        }
    }

    private void Begin()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var utf8 = Marshal.StringToCoTaskMemUTF8("A turn is running");
            try
            {
                var reason = Send(Class("NSString"), Selector("stringWithUTF8String:"), utf8);
                var token = SendOptions(ProcessInfo(), Selector("beginActivityWithOptions:reason:"), UserInitiatedAllowingIdleSystemSleep, reason);
                // The token comes back autoreleased; it is held until End.
                _token = Send(token, Selector("retain"));
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8);
            }
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private void End()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            Send(ProcessInfo(), Selector("endActivity:"), _token);
            Send(_token, Selector("release"));
            _token = IntPtr.Zero;
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static IntPtr ProcessInfo() => Send(Class("NSProcessInfo"), Selector("processInfo"));

    private static IntPtr Class(string name) => objc_getClass(name);

    private static IntPtr Selector(string name) => sel_registerName(name);

    private const string Objc = "/usr/lib/libobjc.A.dylib";

    [DllImport(Objc)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(Objc)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(Objc)]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(Objc)]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendOptions(IntPtr receiver, IntPtr selector, ulong options, IntPtr reason);
}
