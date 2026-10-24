using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Assembly.Mac.Window;

/// <summary>
/// <c>memory.sample</c>: this process's RSS and its WebContent process's, as the Tauri app reports them. WebContent is
/// an XPC service launchd starts, not a child; macOS holds this app responsible for it when the app was started with
/// <c>open</c> (window.sh does), so it is the WebContent process whose responsible pid is this one.
/// </summary>
internal static class MemorySample
{
    public static void Write(Utf8JsonWriter writer)
    {
        var appPid = Environment.ProcessId;
        var webPid = WebContentPid(appPid) ?? throw new InvalidOperationException("no WebContent process for this app");
        writer.WriteStartObject("result");
        writer.WriteNumber("appPid", appPid);
        WriteRss(writer, "appRssKb", appPid);
        writer.WriteNumber("webContentPid", webPid);
        WriteRss(writer, "webContentRssKb", webPid);
        writer.WriteEndObject();
    }

    private static void WriteRss(Utf8JsonWriter writer, string name, int pid)
    {
        if (long.TryParse(Run("ps", "-o", "rss=", "-p", pid.ToString(CultureInfo.InvariantCulture)).Trim(), CultureInfo.InvariantCulture, out var kb))
            writer.WriteNumber(name, kb);
        else
            writer.WriteNull(name);
    }

    // The newest WebContent this app is responsible for.
    private static int? WebContentPid(int appPid) =>
        Run("pgrep", "-f", "com.apple.WebKit.WebContent")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => int.Parse(line, CultureInfo.InvariantCulture))
            .Where(pid => ResponsibleFor(pid) == appPid)
            .Select(pid => (int?)pid)
            .LastOrDefault();

    private static string Run(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "responsibility_get_pid_responsible_for_pid")]
    private static extern int ResponsibleFor(int pid);
}
