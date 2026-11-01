using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
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
        foreach (var column in new[] { "output_source", "acp_session_id" })
            if (scope.QueryFirst("SELECT count(*) FROM pragma_table_info('tool_details') WHERE name=$name;", row => row.Long(0), new SqlArg("$name", column)) == 0)
                scope.Execute($"ALTER TABLE tool_details ADD COLUMN {column} TEXT;");
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

    private StoredToolCall? ReadToolCall(string sessionId, string itemId)
    {
        using var snapshot = _db.OpenSnapshot(CancellationToken.None);
        var detail = snapshot.Query("""
            SELECT (SELECT json_extract(payload,'$.summary') FROM rows WHERE session_id=$session AND item_id=$item AND kind='tool'
                    ORDER BY seq DESC LIMIT 1),input,output,diff,path,output_source,acp_session_id
            FROM tool_details WHERE session_id=$session AND item_id=$item;
            """, row => (Call: new StoredToolCall(Text(row, 0), Text(row, 1), Text(row, 2), Text(row, 3), Text(row, 4)),
                Source: Text(row, 5), Provider: Text(row, 6)), new SqlArg("$session", sessionId), new SqlArg("$item", itemId)).FirstOrDefault();
        if (detail.Call is null) return null;
        if (detail.Provider is null) return detail.Call;
        var terminals = snapshot.Query("""
            WITH matched AS (
              SELECT r.acp_session_id,r.terminal_id,r.ordinal,t.command,t.cwd,t.exit_status,length(t.output) AS preview_bytes,t.output_bytes
              FROM tool_terminal_refs r LEFT JOIN acp_terminal_details t
                ON t.session_id=r.session_id AND t.acp_session_id=r.acp_session_id AND t.terminal_id=r.terminal_id
              WHERE r.session_id=$session AND r.item_id=$item AND r.acp_session_id=$provider),
            bounded AS (
              SELECT acp_session_id,terminal_id,ordinal,command,cwd,exit_status,output_bytes,
                COALESCE(SUM(preview_bytes) OVER (ORDER BY ordinal,terminal_id
                ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING),0) AS preceding_bytes,
                COALESCE(SUM(output_bytes) OVER (),0) AS total_bytes FROM matched)
            SELECT b.acp_session_id,b.terminal_id,b.command,b.cwd,b.exit_status,
              substr(t.output,1,max(0,$cap-b.preceding_bytes)),b.output_bytes,b.total_bytes
            FROM bounded b LEFT JOIN acp_terminal_details t
              ON t.session_id=$session AND t.acp_session_id=b.acp_session_id AND t.terminal_id=b.terminal_id
            ORDER BY b.ordinal,b.terminal_id;
            """, row => (State: new ToolTerminalState(row.Text(0), row.Text(1), Text(row, 2), Text(row, 3),
                row.IsNull(4) ? null : JsonSerializer.Deserialize(row.Text(4), RowJsonContext.Rows.TerminalExit)),
                Bytes: row.IsNull(5) ? null : row.Blob(5), Count: row.IsNull(6) ? (long?)null : row.Long(6), Total: row.Long(7)),
            new SqlArg("$session", sessionId), new SqlArg("$item", itemId), new SqlArg("$provider", detail.Provider), new SqlArg("$cap", RowsFeature.ToolCap));
        var states = terminals.Count == 0 ? null : Array.AsReadOnly(terminals.Select(t => t.State).ToArray());
        if ((detail.Source is null or "raw" && detail.Call.Output is not null) || !terminals.Any(t => t.Count is not null))
            return detail.Call with { Terminals = states };
        using var preview = new MemoryStream();
        var total = terminals[0].Total;
        foreach (var terminal in terminals)
        {
            if (terminal.Bytes is { } bytes)
                preview.Write(bytes, 0, Math.Min(bytes.Length, RowsFeature.ToolCap - (int)preview.Length));
        }
        var raw = preview.ToArray();
        var length = raw.Length;
        if (total > length)
        {
            // Decode no incomplete suffix when the byte preview ends inside a UTF8 code point.
            var start = length - 1;
            while (start >= 0 && (raw[start] & 0xC0) == 0x80) start--;
            if (start >= 0)
            {
                var first = raw[start];
                var needed = first < 0x80 ? 1 : first is >= 0xC2 and <= 0xDF ? 2 : first is >= 0xE0 and <= 0xEF ? 3 : first is >= 0xF0 and <= 0xF4 ? 4 : 1;
                if (length - start < needed) length = start;
            }
        }
        var decoded = Encoding.UTF8.GetString(raw, 0, length);
        var (output, _) = RowsFeature.Cut(decoded);
        return detail.Call with { Output = output, OutputBytes = total, OutputTruncated = total > length || output != decoded, Terminals = states };
    }
}
