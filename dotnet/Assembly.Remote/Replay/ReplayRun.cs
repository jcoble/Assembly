using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Assembly.Infrastructure.Persistence;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Application.Features.Chat;
using Assembly.Application.Common.Acp;
using Assembly.Infrastructure.Transcript;

namespace Assembly.Remote.Replay;

/// <summary>
/// <c>rows TRANSCRIPT [--db PATH]</c>: converts a recorded transcript into rows, saves them, and prints each row
/// as it would be pushed, then what a page load returns and how the sizes compare with the raw ACP.
/// </summary>
public static class ReplayRun
{
    public const string Usage = "usage: Assembly.Remote rows <transcript.jsonl> [--db PATH]";
    private const string Thread = "replay";

    public static int Run(IReadOnlyList<string> args)
    {
        if (args is not ([_] or [_, "--db", _]))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var dbPath = args.Count == 3 ? args[2] : Path.Combine(Path.GetTempPath(), $"replay-{Guid.NewGuid():N}.db");
        using var db = AppDatabase.Open(dbPath);
        var store = RowStore.Open(db);
        var converter = new RowConverter();
        var pushed = new List<StoredRow>();
        var commitTimes = new List<double>();
        foreach (var agentEvent in TranscriptReplay.Events(args[0]))
        {
            var batch = converter.Apply(agentEvent);
            var started = Stopwatch.GetTimestamp();
            var saved = store.Commit(Thread, batch);
            if (!batch.IsEmpty)
                commitTimes.Add(Stopwatch.GetElapsedTime(started).TotalMicroseconds);
            foreach (var row in saved)
            {
                pushed.Add(row);
                Console.WriteLine(Line(row));
            }
        }

        var page = store.PageBefore(Thread, long.MaxValue, 1000);
        Console.WriteLine();
        Console.WriteLine($"page load ({page.Rows.Count} rows, newest form of each item):");
        foreach (var row in page.Rows)
            Console.WriteLine(Line(row));

        var (updates, rawBytes) = RawUpdates(args[0]);
        var pushedBytes = pushed.Sum(row => RowWire.ToJson(row).Length);
        var pageBytes = page.Rows.Sum(row => RowWire.ToJson(row).Length);
        Console.WriteLine();
        Console.WriteLine($"raw ACP:   {updates} session/update lines, {N(rawBytes)} bytes");
        Console.WriteLine($"pushed:    {pushed.Count} rows ({pushed.Count(row => row.Kind == RowKind.Delta)} deltas), {N(pushedBytes)} bytes as JSON");
        Console.WriteLine($"page load: {page.Rows.Count} rows, {N(pageBytes)} bytes as JSON");
        // The first commit also creates the tables' pages and compiles the store's code, so it's shown apart.
        var later = commitTimes.Skip(1).Order().ToList();
        var median = later.Count == 0 ? 0 : later[later.Count / 2];
        Console.WriteLine($"commits:   {commitTimes.Count}; the first took {commitTimes.FirstOrDefault():F0} µs, the rest {median:F0} µs median");
        Console.WriteLine($"database:  {dbPath}");
        return 0;
    }

    private static string Line(StoredRow row)
    {
        var payload = row.Payload.GetRawText();
        if (payload.Length > 140)
            payload = payload[..140] + "…";
        return $"{row.Seq,4} {row.Kind,-8} {row.ItemId ?? "-",-34} {payload}";
    }

    private static (int Lines, long Bytes) RawUpdates(string path)
    {
        var (lines, bytes) = (0, 0L);
        foreach (var line in File.ReadLines(path))
        {
            using var entry = JsonDocument.Parse(line);
            var message = entry.RootElement.GetProperty("line");
            if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("method", out var method) && method.GetString() == "session/update")
                (lines, bytes) = (lines + 1, bytes + message.GetRawText().Length);
        }

        return (lines, bytes);
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
