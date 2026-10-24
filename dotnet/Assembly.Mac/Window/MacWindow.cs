using System.Runtime.InteropServices;
using Photino.NET;
using Assembly.Engine.Agents;
using Assembly.Engine.Api;
using Assembly.Engine.Sessions;
using Assembly.Mac.Remote;
using Assembly.Mac.Serve;

namespace Assembly.Mac.Window;

/// <summary>
/// <c>window --app DIR &lt;serve options&gt;</c>: the backend as <c>serve</c> runs it, and in the same process a Photino
/// window showing the built web app from DIR. Runs on the main thread, which macOS needs for the window, until the
/// window closes. <c>ASSEMBLY_SOAK</c> is the page's query, as in the Tauri app. WebKit's context menu and inspector are
/// off unless <c>ASSEMBLY_DEVTOOLS=1</c>.
/// </summary>
public static class MacWindow
{
    public const string Usage = "usage: Assembly.Mac window --app <web build folder> [serve options]";

    public static int Run(IReadOnlyList<string> args)
    {
        var appDir = args is ["--app", var dir, ..] ? dir : null;
        if (appDir is null || !Directory.Exists(appDir) || MacOptions.Parse(args.Skip(2).ToList()) is not { } options)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var token = TokenFile.ReadOrCreate(options.TokenFile);
        var launcher = new AdapterLauncher(AgentDescriptor.Load(options.AgentsFile), options.Checkouts, new UnclaimedPermissions());
        // Started here, before the window, and stopped after it closes; Kestrel runs on the thread pool meanwhile.
        var (tunnel, remote) = options.Remote is { } host ? RemoteTunnel.ConnectAsync(host, Console.Out).GetAwaiter().GetResult() : (null, null);
        var backend = MacBackend.StartAsync(options.Home, token, options.Port, launcher, Console.Out, remote).GetAwaiter().GetResult();
        // Closing the last window, or Quit, ends the process through Cocoa's terminate and exit(), which never come
        // back here, so the backend (and its adapter) and the tunnel are stopped before the window goes, on every way
        // it can go: exit() runs the atexit handler.
        var stopped = 0;
        void Shutdown(string why)
        {
            if (Interlocked.Exchange(ref stopped, 1) == 1)
                return;
            Console.WriteLine($"window: stopping the backend and the tunnel ({why})");
            backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
            tunnel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown("process exit");
        _atExit = () => Shutdown("exit()");
        _ = atexit(_atExit);
        try
        {
            Console.WriteLine($"listening on {backend.Address}");
            var files = new AppFiles(appDir);
            PhotinoWindow window = null!;
            // The picker runs on the UI thread; Invoke waits for it, so the bridge call waits for the user's choice.
            string[] PickImages()
            {
                string[] picked = [];
                window.Invoke(() => picked = window.ShowOpenFile("Attach images", multiSelect: true, filters: [("Images", ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp"])]) ?? []);
                return picked;
            }
            var bridge = new WindowBridge(token, Path.Combine(Path.GetFullPath(appDir), "..", "..", "soak-results"), backend.Address, PickImages);
            var devTools = Environment.GetEnvironmentVariable("ASSEMBLY_DEVTOOLS") == "1";
            var query = Environment.GetEnvironmentVariable("ASSEMBLY_SOAK") is { Length: > 0 } soak ? "?" + (soak == "1" ? "soak=1" : soak) : "";
            window = new PhotinoWindow()
                .SetLogVerbosity(0)
                .SetTitle("Assembly (Photino)")
                .SetUseOsDefaultSize(false)
                .SetSize(1200, 800)
                .SetMinSize(640, 400)
                .SetContextMenuEnabled(devTools)
                .SetDevToolsEnabled(devTools)
                .RegisterWindowClosingHandler((_, _) =>
                {
                    Shutdown("window closing");
                    return false;
                })
                .RegisterCustomSchemeHandler(AppFiles.Scheme, (object _, string _, string url, out string contentType) => files.Open(url, out contentType))
                .RegisterCustomSchemeHandler("attach", (object _, string _, string url, out string contentType) => bridge.OpenChosen(url, out contentType))
                // Answered off the UI thread; the reply goes back on it.
                .RegisterWebMessageReceivedHandler((sender, message) =>
                {
                    var target = (PhotinoWindow)sender!;
                    _ = Task.Run(async () =>
                    {
                        if (await bridge.AnswerAsync(message) is { } reply)
                            target.Invoke(() => target.SendWebMessage(reply));
                    });
                })
                .Load(new Uri($"{AppFiles.Origin}/{query}"));
            // SIGTERM (window.sh stop) stops the backend and the tunnel, then closes the window.
            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                Shutdown("SIGTERM");
                window.Invoke(window.Close);
            });
            window.WaitForClose();
            return 0;
        }
        finally
        {
            Shutdown("window closed");
        }
    }

    // Kept here so the collector never frees the delegate exit() calls back into.
    private static ExitHandler? _atExit;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ExitHandler();

    [DllImport("libc")]
    private static extern int atexit(ExitHandler handler);
}
