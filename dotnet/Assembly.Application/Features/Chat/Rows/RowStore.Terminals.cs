using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Rows.Copies;
using Assembly.Domain.Events;

namespace Assembly.Application.Features.Chat.Rows;

public sealed partial class RowStore
{
    private static void MigrateTerminalDetails(IWriteScope scope)
    {
        scope.Execute("""
            CREATE TABLE IF NOT EXISTS acp_terminal_details(
              session_id TEXT NOT NULL, acp_session_id TEXT NOT NULL, terminal_id TEXT NOT NULL,
              output BLOB, output_bytes INTEGER, command TEXT, cwd TEXT, exit_status TEXT,
              PRIMARY KEY(session_id, acp_session_id, terminal_id));
            CREATE TABLE IF NOT EXISTS tool_terminal_refs(
              session_id TEXT NOT NULL, item_id TEXT NOT NULL, acp_session_id TEXT NOT NULL, terminal_id TEXT NOT NULL,
              ordinal INTEGER NOT NULL, current INTEGER NOT NULL,
              PRIMARY KEY(session_id, item_id, acp_session_id, terminal_id));
            """);
        // A saved copy's detail keeps its fields' true sizes and whether a field was cut (TSK-1692).
        foreach (var (column, type) in new[] { ("output_source", "TEXT"), ("acp_session_id", "TEXT"), ("output_total", "INTEGER"),
                     ("diff_total", "INTEGER"), ("truncated", "INTEGER NOT NULL DEFAULT 0") })
            if (scope.QueryFirst("SELECT count(*) FROM pragma_table_info('tool_details') WHERE name=$name;", row => row.Long(0), new SqlArg("$name", column)) == 0)
                scope.Execute($"ALTER TABLE tool_details ADD COLUMN {column} {type};");
    }

    private static void UpsertTerminal(IWriteScope scope, string sessionId, TerminalChanged terminal)
    {
        if (terminal.AcpSessionId is not { } provider) return;
        byte[]? bytes = null;
        var outputChanged = terminal.OutputReplaced || terminal.Data is not null;
        try
        {
            if (terminal.Data is { } data) bytes = Convert.FromBase64String(data);
        }
        catch (FormatException)
        {
            // Keep the previous bytes/count; the unmodified notification is already in the raw transcript.
            Console.Error.WriteLine($"session {sessionId[..Math.Min(sessionId.Length, 80)]} terminal {terminal.TerminalId[..Math.Min(terminal.TerminalId.Length, 80)]}: invalid-base64");
            outputChanged = false;
        }
        var oldBytes = scope.QueryFirst("""
            SELECT output_bytes FROM acp_terminal_details
            WHERE session_id=$session AND acp_session_id=$provider AND terminal_id=$terminal;
            """, row => row.IsNull(0) ? (long?)null : row.Long(0),
            new SqlArg("$session", sessionId), new SqlArg("$provider", provider), new SqlArg("$terminal", terminal.TerminalId));
        var total = !outputChanged ? oldBytes : terminal.OutputReplaced ? bytes?.LongLength
            : checked((oldBytes ?? 0) + (bytes?.LongLength ?? 0));
        var exit = terminal.ExitStatus is { } status ? JsonSerializer.Serialize(status, RowJsonContext.Rows.TerminalExit) : null;
        scope.Execute("""
            INSERT INTO acp_terminal_details(session_id, acp_session_id, terminal_id, output, output_bytes, command, cwd, exit_status)
            VALUES($session,$provider,$terminal,$prefix,$total,$command,$cwd,$exit)
            ON CONFLICT(session_id,acp_session_id,terminal_id) DO UPDATE SET
              output=CASE WHEN NOT $output_changed THEN output WHEN $replace THEN $prefix
                ELSE substr(CAST(COALESCE(output,X'') || $prefix AS BLOB),1,$cap) END,
              output_bytes=$total,
              command=CASE WHEN $command_changed THEN $command ELSE command END,
              cwd=CASE WHEN $cwd_changed THEN $cwd ELSE cwd END,
              exit_status=CASE WHEN $exit_changed THEN $exit ELSE exit_status END;
            """, new SqlArg("$session", sessionId), new SqlArg("$provider", provider), new SqlArg("$terminal", terminal.TerminalId),
            new SqlArg("$prefix", outputChanged && bytes is not null ? bytes.AsSpan(0, Math.Min(bytes.Length, RowsFeature.ToolCap)).ToArray() : null),
            new SqlArg("$total", total), new SqlArg("$output_changed", outputChanged), new SqlArg("$replace", terminal.OutputReplaced),
            new SqlArg("$cap", RowsFeature.ToolCap), new SqlArg("$command_changed", terminal.CommandReplaced), new SqlArg("$command", terminal.Command),
            new SqlArg("$cwd_changed", terminal.CwdReplaced), new SqlArg("$cwd", terminal.Cwd),
            new SqlArg("$exit_changed", terminal.ExitStatusReplaced), new SqlArg("$exit", exit));
    }

    private static void UpsertTerminalRefs(IWriteScope scope, string sessionId, ToolDetail detail)
    {
        if (detail.AcpSessionId is not { } provider || detail.TerminalIds is not { } ids) return;
        scope.Execute("UPDATE tool_terminal_refs SET current=0 WHERE session_id=$session AND item_id=$item AND acp_session_id=$provider;",
            new SqlArg("$session", sessionId), new SqlArg("$item", detail.ItemId), new SqlArg("$provider", provider));
        foreach (var id in ids.Distinct())
            scope.Execute("""
                INSERT INTO tool_terminal_refs(session_id,item_id,acp_session_id,terminal_id,ordinal,current)
                VALUES($session,$item,$provider,$terminal,
                  (SELECT COALESCE(MAX(ordinal),-1)+1 FROM tool_terminal_refs
                   WHERE session_id=$session AND item_id=$item AND acp_session_id=$provider),1)
                ON CONFLICT(session_id,item_id,acp_session_id,terminal_id) DO UPDATE SET current=1;
                """, new SqlArg("$session", sessionId), new SqlArg("$item", detail.ItemId), new SqlArg("$provider", provider),
                new SqlArg("$terminal", id));
    }

    /// <summary>
    /// The items' details for another machine's saved copy (TSK-1692), in one read: output, diff and input each cut to
    /// <paramref name="cap"/> bytes at a whole character, with the output's and diff's true sizes. A terminal tool's
    /// output is its terminals' output. An item with no detail, or with a field too big to read, is left out.
    /// </summary>
    public IReadOnlyList<SavedToolDetail> DetailsToSave(string session, IReadOnlyList<string> items, int cap, CancellationToken ct)
    {
        var saved = new List<SavedToolDetail>();
        foreach (var (item, call) in ReadToolCalls(session, items, cap, cap, ct))
        {
            if (call is null) continue;
            var input = call.Input is { } text && Encoding.UTF8.GetByteCount(text) > cap
                ? DecodePrefix(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetByteCount(text), cap, out _) : call.Input;
            var cut = !ReferenceEquals(input, call.Input) || call.OutputTruncated
                || call.OutputBytes > Encoding.UTF8.GetByteCount(call.Output ?? "") || call.DiffBytes > Encoding.UTF8.GetByteCount(call.Diff ?? "");
            saved.Add(new SavedToolDetail(item, call with { Summary = null, Input = input, OutputTruncated = cut }));
        }
        return saved;
    }

    /// <summary>
    /// Each item's saved detail size as a copy read sends it, each field counted to <paramref name="cap"/>, in one read
    /// that loads none of the fields. An item with no detail is left out.
    /// </summary>
    public IReadOnlyDictionary<string, long> DetailSizes(string session, IReadOnlyList<string> items, int cap, CancellationToken ct) =>
        _db.Query("""
            SELECT d.item_id,
              min(COALESCE(length(CAST(d.input AS BLOB)),0),$cap) + min(COALESCE(length(CAST(d.diff AS BLOB)),0),$cap)
                + COALESCE(length(CAST(d.path AS BLOB)),0)
                + min(max(COALESCE(length(CAST(d.output AS BLOB)),0), COALESCE((SELECT SUM(t.output_bytes) FROM tool_terminal_refs r
                    JOIN acp_terminal_details t ON t.session_id=r.session_id AND t.acp_session_id=r.acp_session_id AND t.terminal_id=r.terminal_id
                    WHERE r.session_id=d.session_id AND r.item_id=d.item_id AND r.acp_session_id=d.acp_session_id),0)),$cap)
            FROM tool_details d WHERE d.session_id=$session AND d.item_id IN (SELECT value FROM json_each($items))
            """, row => (row.Text(0), row.Long(1)), ct, new SqlArg("$session", session), new SqlArg("$cap", cap),
            new SqlArg("$items", JsonSerializer.Serialize(items, RowJsonContext.Rows.IReadOnlyListString)))
        .ToDictionary(pair => pair.Item1, pair => pair.Item2);

    private StoredToolCall? ReadToolCall(string sessionId, string itemId, int outputLimit, int diffLimit, CancellationToken ct) =>
        !ReadToolCalls(sessionId, [itemId], outputLimit, diffLimit, ct).TryGetValue(itemId, out var call) ? null
        : call ?? throw new CallFailed("the answer is over 4 MB");

    /// <summary>
    /// Each item's detail in one statement, which fixes the detail, newest summary, terminal identities, counts and
    /// bounded bytes together. An item with no detail is left out; one with a field over 4 MB is there as null.
    /// </summary>
    private Dictionary<string, StoredToolCall?> ReadToolCalls(string sessionId, IReadOnlyList<string> items, int outputLimit, int diffLimit,
        CancellationToken ct)
    {
        var parts = _db.Query("""
            WITH detail AS (
              SELECT d.item_id AS item,input,output,diff,path,output_source,acp_session_id,output_total,diff_total,truncated,
                (SELECT json_extract(payload,'$.summary') FROM rows
                 WHERE session_id=$session AND item_id=d.item_id AND kind='tool' ORDER BY seq DESC LIMIT 1) AS summary
              FROM tool_details d WHERE d.session_id=$session AND d.item_id IN (SELECT value FROM json_each($items))),
            matched AS (
              SELECT d.item,r.acp_session_id,r.terminal_id,r.ordinal,t.command,t.cwd,t.exit_status,t.output,t.output_bytes,
                COALESCE(length(t.output),0) AS preview_bytes
              FROM tool_terminal_refs r JOIN detail d ON d.acp_session_id=r.acp_session_id AND r.item_id=d.item
              LEFT JOIN acp_terminal_details t ON t.session_id=r.session_id
                AND t.acp_session_id=r.acp_session_id AND t.terminal_id=r.terminal_id
              WHERE r.session_id=$session),
            bounded AS (
              SELECT item,acp_session_id,terminal_id,command,cwd,exit_status,output,output_bytes,
                ROW_NUMBER() OVER (PARTITION BY item ORDER BY ordinal,terminal_id) AS position,
                COALESCE(SUM(preview_bytes) OVER (PARTITION BY item ORDER BY ordinal,terminal_id
                  ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING),0) AS preceding_bytes
              FROM matched),
            chosen AS (
              SELECT item,summary,input,output,diff,path,truncated,
                CASE WHEN (output_source IS NULL OR output_source='raw') AND output IS NOT NULL THEN 0
                  WHEN EXISTS(SELECT 1 FROM matched m WHERE m.item=detail.item AND m.output_bytes IS NOT NULL) THEN 1 ELSE 0 END AS terminal_output,
                COALESCE((SELECT SUM(m.output_bytes) FROM matched m WHERE m.item=detail.item),0) AS terminal_total,
                COALESCE(output_total,length(CAST(output AS BLOB)),0) AS output_total,
                COALESCE(diff_total,length(CAST(diff AS BLOB)),0) AS diff_total
              FROM detail)
            SELECT CASE WHEN COALESCE(b.position,1)=1 THEN 1 ELSE 0 END,
              COALESCE(length(CAST(d.summary AS BLOB)),0),COALESCE(length(CAST(d.input AS BLOB)),0),COALESCE(length(CAST(d.path AS BLOB)),0),
              CASE WHEN COALESCE(b.position,1)=1 AND length(CAST(d.summary AS BLOB))<=$field_max THEN CAST(d.summary AS BLOB) END,
              CASE WHEN COALESCE(b.position,1)=1 AND length(CAST(d.input AS BLOB))<=$field_max THEN CAST(d.input AS BLOB) END,
              CASE WHEN COALESCE(b.position,1)=1 AND length(CAST(d.path AS BLOB))<=$field_max THEN CAST(d.path AS BLOB) END,
              CASE WHEN COALESCE(b.position,1)=1 AND d.terminal_output=0 AND d.output IS NOT NULL
                THEN COALESCE(substr(CAST(d.output AS BLOB),1,$output_limit),X'') END,
              CASE WHEN COALESCE(b.position,1)=1 AND d.diff IS NOT NULL
                THEN COALESCE(substr(CAST(d.diff AS BLOB),1,$diff_limit),X'') END,
              CASE WHEN d.terminal_output=1 THEN d.terminal_total ELSE d.output_total END,d.diff_total,d.terminal_output,
              b.acp_session_id,b.terminal_id,
              COALESCE(length(CAST(b.command AS BLOB)),0),COALESCE(length(CAST(b.cwd AS BLOB)),0),COALESCE(length(CAST(b.exit_status AS BLOB)),0),
              CASE WHEN length(CAST(b.command AS BLOB))<=$field_max THEN CAST(b.command AS BLOB) END,
              CASE WHEN length(CAST(b.cwd AS BLOB))<=$field_max THEN CAST(b.cwd AS BLOB) END,
              CASE WHEN length(CAST(b.exit_status AS BLOB))<=$field_max THEN CAST(b.exit_status AS BLOB) END,
              CASE WHEN d.terminal_output=1 THEN substr(b.output,1,max(0,$terminal_limit-b.preceding_bytes)) END,
              d.truncated,d.item
            FROM chosen d LEFT JOIN bounded b ON b.item=d.item ORDER BY d.item,b.position;
            """, row =>
        {
            ct.ThrowIfCancellationRequested();
            var tooBig = new[] { 1, 2, 3, 14, 15, 16 }.Any(column => row.Long(column) > 4 * 1024 * 1024);
            var call = row.Long(0) == 1 ? new StoredToolCall(BlobText(row, 4), BlobText(row, 5),
                Prefix(row, 7, row.Long(9), outputLimit), Prefix(row, 8, row.Long(10), diffLimit), BlobText(row, 6),
                row.Long(9), row.Long(10), row.Long(21) != 0) : null;
            var state = row.IsNull(13) ? null : new ToolTerminalState(row.Text(12), row.Text(13), BlobText(row, 17), BlobText(row, 18),
                row.IsNull(19) ? null : JsonSerializer.Deserialize(BlobText(row, 19)!, RowJsonContext.Rows.TerminalExit));
            return new ToolPart(row.Text(22), tooBig, call, state, row.IsNull(20) ? null : row.Blob(20), row.Long(11) != 0);
        }, ct, new SqlArg("$session", sessionId), new SqlArg("$output_limit", outputLimit),
            new SqlArg("$items", JsonSerializer.Serialize(items, RowJsonContext.Rows.IReadOnlyListString)),
            new SqlArg("$diff_limit", diffLimit), new SqlArg("$terminal_limit", Math.Min(outputLimit, RowsFeature.ToolCap)),
            new SqlArg("$field_max", 4 * 1024 * 1024));
        // The rows come grouped by item, each item's first row carrying its detail.
        return parts.GroupBy(part => part.Item).ToDictionary(group => group.Key,
            group => group.Any(part => part.TooBig) ? null : Assemble([.. group], outputLimit));
    }

    // One row of the tool read: its item, whether a field is over 4 MB, the detail (first row), a terminal and its bytes.
    private sealed record ToolPart(string Item, bool TooBig, StoredToolCall? Call, ToolTerminalState? State, byte[]? Bytes, bool TerminalOutput);

    // An item's detail from its rows: a terminal tool's output is its terminals' bytes, in order, cut to the limit.
    private static StoredToolCall Assemble(IReadOnlyList<ToolPart> parts, int outputLimit)
    {
        var detail = parts[0].Call!;
        var terminalStates = parts.Where(part => part.State is not null).Select(part => part.State!).ToArray();
        var states = terminalStates.Length == 0 ? null : Array.AsReadOnly(terminalStates);
        if (!parts[0].TerminalOutput) return detail with { Terminals = states };
        using var preview = new MemoryStream();
        foreach (var part in parts)
            if (part.Bytes is { } bytes) preview.Write(bytes);
        var raw = preview.ToArray();
        var output = DecodePrefix(raw, detail.OutputBytes, Math.Min(outputLimit, RowsFeature.ToolCap), out var cut);
        return detail with
        {
            Output = output,
            OutputTruncated = detail.OutputTruncated || detail.OutputBytes > raw.LongLength || cut,
            Terminals = states
        };
    }

    private static string? BlobText(IRow row, int column) => row.IsNull(column) ? null : Encoding.UTF8.GetString(row.Blob(column));

    private static string? Prefix(IRow row, int column, long total, int limit) =>
        row.IsNull(column) ? null : DecodePrefix(row.Blob(column), total, limit, out _);

    private static string DecodePrefix(byte[] raw, long total, int limit, out bool cut)
    {
        var length = raw.Length;
        if (total > length && length > 0)
        {
            var start = length - 1;
            while (start >= 0 && (raw[start] & 0xC0) == 0x80) start--;
            if (start >= 0)
            {
                var first = raw[start];
                var needed = first < 0x80 ? 1 : first is >= 0xC2 and <= 0xDF ? 2 : first is >= 0xE0 and <= 0xEF ? 3 : first is >= 0xF0 and <= 0xF4 ? 4 : 1;
                if (length - start < needed) length = start;
            }
        }
        cut = length < raw.Length;
        var decoded = Encoding.UTF8.GetString(raw, 0, length);
        // Terminal bytes can be malformed; replacement characters may expand beyond the transfer window.
        if (Encoding.UTF8.GetByteCount(decoded) <= limit) return decoded;
        cut = true;
        var characters = 0; var bytes = 0;
        foreach (var rune in decoded.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > limit) break;
            bytes += rune.Utf8SequenceLength; characters += rune.Utf16SequenceLength;
        }
        return decoded[..characters];
    }
}
