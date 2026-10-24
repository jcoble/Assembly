using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Assembly.Mac.Page;

/// <summary>
/// <c>memory.sample</c>: the RSS of the app (the shell that started this backend), of its WebContent process, and of
/// this backend. WebContent is an XPC service launchd starts, not a child; macOS holds the app responsible for it and
/// for this backend, so the app is this process's responsible pid and WebContent is the one the app is responsible for.
/// Elsewhere (a lane on the workbox, headless) there's no app, so only the backend is measured.
/// </summary>
internal static class MemorySample
{
    public static void Write(Utf8JsonWriter writer)
    {
        if (!OperatingSystem.IsMacOS())
        {
            writer.WriteStartObject("result");
            writer.WriteNull("appPid");
            writer.WriteNull("appRssKb");
            writer.WriteNull("webContentPid");
            writer.WriteNull("webContentRssKb");
            writer.WriteNumber("backendPid", Environment.ProcessId);
            WriteRss(writer, "backendRssKb", Environment.ProcessId);
            writer.WriteEndObject();
            return;
        }
        var appPid = ResponsibleFor(Environment.ProcessId);
        var webPid = WebContentPid(appPid) ?? throw new InvalidOperationException("no WebContent process for this app");
        writer.WriteStartObject("result");
        writer.WriteNumber("appPid", appPid);
        WriteRss(writer, "appRssKb", appPid);
        writer.WriteNumber("webContentPid", webPid);
        WriteRss(writer, "webContentRssKb", webPid);
        writer.WriteNumber("backendPid", Environment.ProcessId);
        WriteRss(writer, "backendRssKb", Environment.ProcessId);
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
