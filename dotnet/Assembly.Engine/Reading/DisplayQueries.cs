using System.Text.Json;

namespace Assembly.Engine.Reading;

/// <summary>Only these columns may leave current-state tables through display reads.</summary>
internal static class DisplayQueries
{
    private const string Common = "id,version,machine,created_ms,updated_ms,task_url";
    private static string Columns(string kind) => Common + "," + (kind switch
    {
        "session" => "provider,model,folder,title,state,activity_state,parent_id,run_id,holder,epoch,cancel_requested,lost_ms",
        "run" => "owner_session_id,worktree,branch,pr_id,state,owner_loss_limit_ms,owner_lost_ms",
        "step" => "run_id,name,session_id,state,attempt,max_attempts,required,rule_skippable,started_ms,ended_ms,outcome_reason",
        "needs_me" => "session_id,run_id,step_id,kind,text,options_json,state,deadline_ms,answered_by,answered_ms",
        "message" => "from_session_id,sender_kind,to_session_id,run_id,step_id,text,mode,state,reason,expires_ms",
        "handoff" => "session_id,old_holder,new_holder,old_epoch,next_epoch,state,reason",
        "pr" => "run_id,repo,number,head,state,reported_by,reported_state,observed_head,observed_ms,url",
        "tool_call" => "session_id,run_id,step_id,attempt,epoch,tool,input_preview,output_preview,input_bytes,output_bytes,started_ms,ended_ms,outcome,late",
        "usage" => "session_id,run_id,step_id,attempt,epoch,input_tokens,output_tokens,cached_tokens,cost_nanodollars,duration_ms,late",
        _ => throw new ArgumentException("Unknown entity kind", nameof(kind))
    });

    internal static string Json(string kind)
    {
        var fields = Columns(kind).Split(',').Select(column =>
            $"'{CamelCase(column)}',t.{column}");
        if (kind == "message")
        {
            fields = fields.Select(field => field == "'text',t.text"
                ? "'text',CASE WHEN t.framed=0 THEN substr(t.text,1,256) ELSE t.text END" : field);
            return $"json_object('entityKind','{kind}',{string.Join(",", fields)}," +
                "'framed',t.framed,'clientId',t.client_id,'providerTurnId',t.provider_turn_id," +
                "'textComplete',CASE WHEN t.framed=1 OR length(CAST(substr(t.text,1,256) AS BLOB))=length(CAST(t.text AS BLOB)) THEN 1 ELSE 0 END," +
                "'textUtf8Bytes',length(CAST(t.text AS BLOB)),'attachmentJsonUtf8Bytes',length(CAST(t.attachment_ids_json AS BLOB))," +
                "'attachmentCount',json_array_length(t.attachment_ids_json),'ownerContentSha256',t.owner_content_sha256," +
                "'targetEpoch',t.target_epoch,'heldEpoch',t.held_epoch)";
        }
        return $"json_object('entityKind','{kind}',{string.Join(",", fields)})";
    }

    internal static string MirrorJson(string kind) => kind == "handoff"
        ? Json(kind)[..^1] + ",'runId',(SELECT run_id FROM engine_session WHERE id=t.session_id))"
        : Json(kind);

    private static string CamelCase(string column)
    {
        var parts = column.Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    internal static string[] Kinds(string topic) => topic switch
    {
        "mirror" => ["session", "run", "step", "needs_me", "message", "handoff", "pr"],
        "runs" => ["run", "step", "handoff"],
        "sessions" => ["session", "handoff"],
        "needs_me" => ["needs_me"],
        "messages" => ["message"],
        "prs" => ["pr"],
        "usage" => ["usage", "tool_call"],
        _ => throw new ArgumentException("Unknown topic", nameof(topic))
    };

    internal static string KeyPredicate(string topic, string kind) => (topic, kind) switch
    {
        ("runs", "step") => "t.run_id=$key",
        ("runs", "handoff") => "EXISTS(SELECT 1 FROM engine_session s WHERE s.id=t.session_id AND s.run_id=$key)",
        ("sessions", "handoff") => "t.session_id=$key",
        _ => "t.id=$key"
    };

    internal static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement.Clone();
        if (JsonSerializer.SerializeToUtf8Bytes(item, EngineJson.Default.JsonElement).Length > 32 * 1024)
            throw new InvalidOperationException("Display item exceeds the 32 KiB UTF-8 budget.");
        return item;
    }
}
