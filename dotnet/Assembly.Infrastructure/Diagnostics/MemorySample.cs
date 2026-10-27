using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

using Assembly.Application.Features.Diagnostics;
namespace Assembly.Infrastructure.Diagnostics;

/// <summary>
/// <c>memory.sample</c>: the RSS of the app (the shell that started this backend), of its WebContent process, and of
/// this backend. WebContent is an XPC service launchd starts, not a child; macOS holds the app responsible for it and
/// for this backend, so the app is this process's responsible pid and WebContent is the one the app is responsible for.
/// Elsewhere (a lane on the workbox, headless) there's no app, so only the backend is measured.
/// </summary>
internal static class MemorySample
{
    public static MemoryReading Read()
    {
        var backend = Environment.ProcessId;
        if (!OperatingSystem.IsMacOS())
            return new MemoryReading(null, null, null, null, backend, Rss(backend));
        var appPid = ResponsibleFor(backend);
        var webPid = WebContentPid(appPid) ?? throw new InvalidOperationException("no WebContent process for this app");
        return new MemoryReading(appPid, Rss(appPid), webPid, Rss(webPid), backend, Rss(backend));
    }

    internal static MemoryReading Reading(int backend, int? appPid, int? webPid, Func<int, long?> rss) => throw new NotImplementedException();

    public static void Write(Utf8JsonWriter writer)
    {
        var reading = Read();
        writer.WriteStartObject("result");
        WriteNumber(writer, "appPid", reading.AppPid);
        WriteNumber(writer, "appRssKb", reading.AppRssKb);
        WriteNumber(writer, "webContentPid", reading.WebContentPid);
        WriteNumber(writer, "webContentRssKb", reading.WebContentRssKb);
        writer.WriteNumber("backendPid", reading.BackendPid);
        WriteNumber(writer, "backendRssKb", reading.BackendRssKb);
        writer.WriteEndObject();
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number)
            writer.WriteNumber(name, number);
        else
            writer.WriteNull(name);
    }

    private static long? Rss(int pid) =>
        long.TryParse(Run("ps", "-o", "rss=", "-p", pid.ToString(CultureInfo.InvariantCulture)).Trim(), CultureInfo.InvariantCulture, out var kb)
            ? kb
            : null;

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
