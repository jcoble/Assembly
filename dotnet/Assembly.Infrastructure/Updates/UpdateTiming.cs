using System.Diagnostics;

namespace Assembly.Infrastructure.Updates;

/// <summary>
/// How long each stage of an app update took, one plain line each in <c>app-update/update.log</c>, from the app and from the
/// helper alike. Only the log: nothing shows it. A line that can't be written is left out.
/// </summary>
internal static class UpdateTiming
{
    internal const string LogName = "update.log";

    /// <param name="since">A <see cref="Stopwatch.GetTimestamp"/> from the stage's start.</param>
    internal static void Note(string folder, string stage, long since)
    {
        var took = Stopwatch.GetElapsedTime(since).TotalMilliseconds;
        try
        {
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, LogName), $"{DateTimeOffset.UtcNow:O} update timing: {stage} {took:F0} ms\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
