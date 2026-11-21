using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;

namespace Assembly.Application.Features.Chat.Rows.Copies;

/// <summary>One tool item's detail as a saved copy keeps it: each field cut, with its true size (TSK-1692).</summary>
public sealed record SavedToolDetail([property: JsonRequired] string Item, [property: JsonRequired] StoredToolCall Call);

/// <summary>Tool details a saved copy downloads with its rows, saved in the same write as them (TSK-1692).</summary>
public sealed partial class CopyRecords
{
    /// <summary>
    /// Saves downloaded details in the caller's write, each over what was saved before: the output as text with its true
    /// size, and each terminal's command, working folder and exit.
    /// </summary>
    internal static void SaveDetails(IWriteScope scope, string session, IReadOnlyList<SavedToolDetail>? details)
    {
        foreach (var (item, call) in details ?? [])
        {
            var provider = call.Terminals is [{ } first, ..] ? first.AcpSessionId : null;
            scope.Execute("""
                INSERT INTO tool_details(session_id,item_id,input,output,diff,path,output_source,acp_session_id,output_total,diff_total,truncated)
                VALUES($session,$item,$input,$output,$diff,$path,'raw',$provider,$output_total,$diff_total,$truncated)
                ON CONFLICT(session_id,item_id) DO UPDATE SET input=excluded.input, output=excluded.output, diff=excluded.diff,
                  path=excluded.path, output_source=excluded.output_source, acp_session_id=excluded.acp_session_id,
                  output_total=excluded.output_total, diff_total=excluded.diff_total, truncated=excluded.truncated
                """, new SqlArg("$session", session), new SqlArg("$item", item), new SqlArg("$input", call.Input),
                new SqlArg("$output", call.Output), new SqlArg("$diff", call.Diff), new SqlArg("$path", call.Path),
                new SqlArg("$provider", provider), new SqlArg("$output_total", call.OutputBytes), new SqlArg("$diff_total", call.DiffBytes),
                new SqlArg("$truncated", call.OutputTruncated));
            SaveTerminals(scope, session, item, call.Terminals ?? []);
        }
    }

    // A terminal's output is in its tool's saved output, so only what it ran, where, and how it ended is kept here.
    private static void SaveTerminals(IWriteScope scope, string session, string item, IReadOnlyList<ToolTerminalState> terminals)
    {
        for (var ordinal = 0; ordinal < terminals.Count; ordinal++)
        {
            var terminal = terminals[ordinal];
            var exit = terminal.ExitStatus is { } status ? JsonSerializer.Serialize(status, RowJsonContext.Rows.TerminalExit) : null;
            scope.Execute("""
                INSERT INTO acp_terminal_details(session_id,acp_session_id,terminal_id,command,cwd,exit_status)
                VALUES($session,$provider,$terminal,$command,$cwd,$exit)
                ON CONFLICT(session_id,acp_session_id,terminal_id) DO UPDATE SET command=excluded.command, cwd=excluded.cwd,
                  exit_status=excluded.exit_status;
                """, new SqlArg("$session", session), new SqlArg("$provider", terminal.AcpSessionId), new SqlArg("$terminal", terminal.TerminalId),
                new SqlArg("$command", terminal.Command), new SqlArg("$cwd", terminal.Cwd), new SqlArg("$exit", exit));
            scope.Execute("""
                INSERT INTO tool_terminal_refs(session_id,item_id,acp_session_id,terminal_id,ordinal,current)
                VALUES($session,$item,$provider,$terminal,$ordinal,1)
                ON CONFLICT(session_id,item_id,acp_session_id,terminal_id) DO UPDATE SET ordinal=excluded.ordinal, current=1;
                """, new SqlArg("$session", session), new SqlArg("$item", item), new SqlArg("$provider", terminal.AcpSessionId),
                new SqlArg("$terminal", terminal.TerminalId), new SqlArg("$ordinal", ordinal));
        }
    }
}
