using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine.Persistence;

namespace Assembly.Application.Features.Engine.Retention;

public sealed record RetentionResult(long RetainedAfter, long RemovedEvents, long ClearedPreviewBytes);

/// <summary>Only previews and delivered, acknowledged events are disposable. Uses the worker's writer transaction.</summary>
public sealed class EngineRetention(Func<long> publishedThrough, Func<long> minimumReaderAck,
    Action<long> invalidateReadersBefore) : IEngineRetention
{
    private const long PreviewAgeMs = 7L * 24 * 60 * 60 * 1000;
    private const int PreviewLimit = 4096;
    private const int MachinePreviewLimit = 16 * 1024 * 1024;
    private const long KeptEvents = 10000;
    private const long CompactAt = 11000;
    private const string PreviewBytes = "COALESCE(length(CAST(input_preview AS BLOB)),0)+COALESCE(length(CAST(output_preview AS BLOB)),0)";

    public long? NextDeadlineMs(IReadScope s, long nowMs) => s.QueryFirst("""
        SELECT MIN(COALESCE(ended_ms,started_ms))+$age FROM engine_tool_call
        WHERE input_preview IS NOT NULL OR output_preview IS NOT NULL
        """, r => r.IsNull(0) ? (long?)null : r.Long(0), new SqlArg("$age", PreviewAgeMs));

    public void Apply(IWriteScope s, long nowMs)
    {
        var high = s.QueryFirst("SELECT next_seq-1 FROM engine_meta WHERE singleton=1", r => r.Long(0));
        // A reader may pin one backlog, but must reset before pinning a second one.
        if (high >= 2 * CompactAt)
            invalidateReadersBefore(high - 2 * CompactAt + 1);
        Apply(s, nowMs, publishedThrough(), minimumReaderAck());
    }

    public static RetentionResult Apply(IWriteScope scope, long nowMs, long publishedThrough, long minimumReaderAck)
    {
        var beforeBytes = TotalPreviewBytes(scope);
        scope.Execute("""
            UPDATE engine_tool_call SET input_preview=NULL,output_preview=NULL
            WHERE COALESCE(ended_ms,started_ms)<=$before
                AND (input_preview IS NOT NULL OR output_preview IS NOT NULL)
            """, new SqlArg("$before", nowMs - PreviewAgeMs));
        // Recording already creates bounded, structural previews. Also enforce the limit on imported rows.
        var oversized = scope.Query("""
            SELECT id,input_preview,output_preview FROM engine_tool_call
            WHERE length(CAST(input_preview AS BLOB))>$limit OR length(CAST(output_preview AS BLOB))>$limit
            """, r => (Id: r.Text(0), Input: r.IsNull(1) ? null : r.Text(1), Output: r.IsNull(2) ? null : r.Text(2)),
            new SqlArg("$limit", PreviewLimit));
        foreach (var row in oversized)
            scope.Execute("UPDATE engine_tool_call SET input_preview=$input,output_preview=$output WHERE id=$id",
                new("$id", row.Id), new("$input", BoundPreview(row.Input)), new("$output", BoundPreview(row.Output)));
        if (TotalPreviewBytes(scope) > MachinePreviewLimit)
            scope.Execute($"""
                WITH newest AS (
                    SELECT id,SUM({PreviewBytes}) OVER (
                        ORDER BY COALESCE(ended_ms,started_ms) DESC,id DESC ROWS UNBOUNDED PRECEDING) bytes
                    FROM engine_tool_call WHERE input_preview IS NOT NULL OR output_preview IS NOT NULL)
                UPDATE engine_tool_call SET input_preview=NULL,output_preview=NULL
                WHERE id IN (SELECT id FROM newest WHERE bytes>$limit)
                """, new SqlArg("$limit", MachinePreviewLimit));

        var meta = scope.QueryFirst("SELECT next_seq-1,retained_after FROM engine_meta WHERE singleton=1",
            r => (High: r.Long(0), Floor: r.Long(1)));
        var removed = 0;
        var floor = meta.Floor;
        if (meta.High - floor >= CompactAt)
        {
            floor = Math.Max(floor, Math.Min(meta.High - KeptEvents, Math.Min(publishedThrough, minimumReaderAck)));
            if (floor > meta.Floor)
            {
                // Current tables are the durable checkpoint at this high-water; sequence allocation never rewinds.
                scope.Execute("UPDATE engine_meta SET snapshot_seq=$high,retained_after=$floor WHERE singleton=1",
                    new("$high", meta.High), new("$floor", floor));
                removed = scope.Execute("DELETE FROM engine_event WHERE seq<=$floor", new SqlArg("$floor", floor));
            }
        }
        return new(floor, removed, beforeBytes - TotalPreviewBytes(scope));
    }

    private static long TotalPreviewBytes(IReadScope scope) =>
        scope.QueryFirst($"SELECT COALESCE(SUM({PreviewBytes}),0) FROM engine_tool_call", r => r.Long(0));

    private static string? BoundPreview(string? preview)
    {
        if (preview is null || Encoding.UTF8.GetByteCount(preview) <= PreviewLimit) return preview;
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in preview.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > PreviewLimit) break;
            result.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }
}
