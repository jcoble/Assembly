using System.Runtime.InteropServices;

namespace Assembly.Application.Features.Diagnostics;

/// <summary>
/// <c>activity.begin</c> / <c>activity.end</c>: while a turn runs, an <c>NSProcessInfo</c> activity keeps App Nap from
/// throttling the app, as the Tauri app does. A user-initiated activity that still allows idle system sleep, so the
/// Mac and its display may still sleep. A second begin while one is held does nothing.
/// </summary>
internal sealed class TurnActivity
{
    // NSActivityUserInitiatedAllowingIdleSystemSleep.
    private const ulong UserInitiatedAllowingIdleSystemSleep = 0x00EFFFFF;

    private readonly Lock _lock = new();
    private IntPtr _token;

    public bool Held
    {
        get { lock (_lock) return _token != IntPtr.Zero; }
    }

    public void Begin()
    {
        lock (_lock)
        {
            if (_token != IntPtr.Zero)
                return;
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
    }

    public void End()
    {
        lock (_lock)
        {
            if (_token == IntPtr.Zero)
                return;
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
