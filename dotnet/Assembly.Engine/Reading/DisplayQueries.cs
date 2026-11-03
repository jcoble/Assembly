using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using Assembly.Application.Common.Database;

namespace Assembly.Engine.Reading;

/// <summary>Only these columns may leave current-state tables through display reads.</summary>
internal static class DisplayQueries
{
    private const string Common = "id,version,machine,created_ms,updated_ms,task_url";
    private static string Columns(string kind) => Common + "," + (kind switch
    {
        "session" => "provider,model,folder,title,state,activity_state,parent_id,run_id,holder,epoch,cancel_requested,lost_ms,project_id,branch,checkout_state,checkout_operation_id",
        "run" => "owner_session_id,worktree,branch,pr_id,state,owner_loss_limit_ms,owner_lost_ms," + Workflows.Reading.WorkflowReadModels.RunColumns,
        "step" => "run_id,name,session_id,state,attempt,max_attempts,required,rule_skippable,started_ms,ended_ms,outcome_reason," + Workflows.Reading.WorkflowReadModels.StepColumns,
        "needs_me" => "session_id,run_id,step_id,kind,text,options_json,state,deadline_ms,answered_by,answered_ms,addressee_session_id",
        "message" => "from_session_id,sender_kind,to_session_id,run_id,step_id,text,mode,state,reason,expires_ms",
        "handoff" => "session_id,old_holder,new_holder,old_epoch,next_epoch,state,reason",
        "pr" => "run_id,owner_session_id,repo,number,head,state,reported_by,reported_state,observed_head,observed_ms,url",
        "pr_observation" => "origin_machine,coordinator_machine,session_id,run_id,step_id,epoch,state,coordinator_seq",
        "tool_call" => "session_id,run_id,step_id,attempt,epoch,tool,input_preview,output_preview,input_bytes,output_bytes,started_ms,ended_ms,outcome,late",
        "usage" => "session_id,run_id,step_id,attempt,epoch,input_tokens,output_tokens,cached_tokens,cost_nanodollars,duration_ms,late",
        "provider" => "provider,account_context,installation,auth_state,reason,facts_json,checked_ms",
        "sign_in" => "provider,account_context,method_id,state,reason,deadline_ms,origin_need_id",
        _ => throw new ArgumentException("Unknown entity kind", nameof(kind))
    });

    internal static string Json(string kind)
    {
        if (kind == "needs_me") return """
            json_object('entityKind','needs_me','id',t.id,'version',t.version,'machine',t.machine,
                'sessionId',t.session_id,'runId',t.run_id,'stepId',t.step_id,'addresseeSessionId',t.addressee_session_id,
                'addresseeMachine',(SELECT s.machine FROM engine_session s WHERE s.id=t.addressee_session_id),
                'chatRowId',t.chat_row_id,'kind',t.kind,'text',NULL,
                'optionsJson',CASE WHEN length(CAST(t.options_json AS BLOB))<=32768 THEN t.options_json END,
                'title',json_extract(t.presentation_json,'$.title'),'recommendation',json_extract(t.presentation_json,'$.recommendation'),
                'displayNumber',t.display_number,'inputMode',json_extract(t.presentation_json,'$.inputMode'),
                'schema',json_extract(t.presentation_json,'$.schema'),'options',json_extract(t.presentation_json,'$.options'),
                'freeText',json(CASE json_extract(t.presentation_json,'$.freeText') WHEN 1 THEN 'true' WHEN 0 THEN 'false' ELSE 'null' END),'state',t.state,'answer',json(CASE WHEN length(CAST(t.answer_json AS BLOB))<=32768 THEN t.answer_json END),
                'answerComplete',json(CASE WHEN t.answer_json IS NULL THEN 'null' WHEN length(CAST(t.answer_json AS BLOB))<=32768 THEN 'true' ELSE 'false' END),
                'answerUtf8Bytes',length(CAST(t.answer_json AS BLOB)),
                'answerText',CASE WHEN length(CAST(t.answer_json AS BLOB))<=32768 AND json_type(t.answer_json)='text' THEN json_extract(t.answer_json,'$') END,
                'answeredBy',t.answered_by,'answeredMs',t.answered_ms,'answeredOrigin',json(t.answered_origin_json))
            """.Replace("t.presentation_json", "CASE WHEN length(CAST(t.presentation_json AS BLOB))<=32768 THEN t.presentation_json END", StringComparison.Ordinal);
        var fields = Columns(kind).Split(',').Select(column =>
            $"'{CamelCase(column)}',t.{column}");
        if (kind is "run" or "step")
        {
            fields = fields.Select(field => field == "'detail',t.detail" ? """
                'detail',COALESCE(t.detail,(SELECT json_extract(spec.value,'$.display.detail') FROM engine_workflow_snapshot snap,
                  json_each(snap.snapshot_json,'$.snapshot.definition.steps') spec WHERE snap.run_id=t.run_id AND spec.key=t.definition_key))
                """ : field);
            fields = fields.Select(field => field is "'paused',t.paused" or "'scopeGranted',t.scope_granted" or "'closed',t.closed"
                ? field[..(field.IndexOf(',') + 1)] + "json(CASE WHEN " + field[(field.IndexOf(',') + 1)..] + "=1 THEN 'true' ELSE 'false' END)" : field);
            return Workflows.Reading.WorkflowReadModels.Extend(kind, $"json_object('entityKind','{kind}',{string.Join(",", fields)})");
        }
        if (kind == "session") fields = fields.Select(field => field == "'title',t.title" ? "'title',NULL" : field);
        if (kind == "needs_me") fields = fields.Select(field => field == "'text',t.text" ? "'text',NULL" : field);
        if (kind == "session") return $"json_object('entityKind','{kind}',{string.Join(",", fields)}," +
            "'parentMachine',(SELECT parent.machine FROM engine_session parent WHERE parent.id=t.parent_id))";
        if (kind == "message")
        {
            fields = fields.Select(field => field == "'text',t.text"
                ? "'text',CASE WHEN t.framed=0 THEN substr(t.text,1,256) ELSE '' END" : field);
            return $"json_object('entityKind','{kind}',{string.Join(",", fields)}," +
                "'framed',t.framed,'clientId',t.client_id,'providerTurnId',t.provider_turn_id,'inFlight',t.in_flight," +
                "'textComplete',CASE WHEN t.framed=0 AND length(CAST(substr(t.text,1,256) AS BLOB))=length(CAST(t.text AS BLOB)) THEN 1 ELSE 0 END," +
                "'textUtf8Bytes',length(CAST(t.text AS BLOB)),'attachmentJsonUtf8Bytes',length(CAST(t.attachment_ids_json AS BLOB))," +
                "'attachmentCount',json_array_length(t.attachment_ids_json),'ownerContentSha256',t.owner_content_sha256," +
                "'targetEpoch',t.target_epoch,'heldEpoch',t.held_epoch)";
        }
        return $"json_object('entityKind','{kind}',{string.Join(",", fields)})";
    }

    internal static string ObservationDetails => Json("pr_observation")[..^1] + ",'facts',json(t.facts_json))";

    internal static string MirrorJson(string kind) => kind == "handoff"
        ? Json(kind)[..^1] + ",'runId',(SELECT run_id FROM engine_session WHERE id=t.session_id))"
        : Json(kind);

    // Fixed projections: fetch only bounded bytes alongside metadata in the same SQL row.
    internal static string PrefixColumns(string kind) => kind switch
    {
        "session" => "COALESCE(substr(CAST(t.title AS BLOB),1,1027),X'') AS prefix,COALESCE(length(CAST(t.title AS BLOB)),0) AS total,NULL AS label_prefix,NULL AS label_total",
        "needs_me" => "COALESCE(substr(CAST(t.text AS BLOB),1,32771),X'') AS prefix,length(CAST(t.text AS BLOB)) AS total,NULL AS label_prefix,NULL AS label_total",
        "message" => "CASE WHEN t.framed=1 THEN COALESCE(substr(CAST(t.text AS BLOB),1,32771),X'') END AS prefix,CASE WHEN t.framed=1 THEN length(CAST(t.text AS BLOB)) END AS total,CASE WHEN t.framed=1 THEN COALESCE(substr(CAST(t.sender_label AS BLOB),1,1027),X'') END AS label_prefix,CASE WHEN t.framed=1 THEN length(CAST(t.sender_label AS BLOB)) END AS label_total",
        _ => "NULL AS prefix,NULL AS total,NULL AS label_prefix,NULL AS label_total"
    };

    internal static JsonElement Project(IRow row, int jsonColumn)
    {
        var json = row.Text(jsonColumn);
        if (row.IsNull(jsonColumn + 1)) return Parse(json);
        var item = JsonNode.Parse(json)!.AsObject();
        var prefix = row.Blob(jsonColumn + 1);
        var total = row.Long(jsonColumn + 2);
        var text = Utf8Prefix(prefix, total);
        var kind = item["entityKind"]!.GetValue<string>();
        if (kind == "session")
        {
            var preview = Scalars(text, 256);
            var complete = prefix.LongLength == total && preview == text;
            item["title"] = preview;
            if (!complete) { item["titleComplete"] = false; item["titleUtf8Bytes"] = total; }
        }
        else if (kind == "needs_me")
        {
            item["text"] = text;
            if (Encoding.UTF8.GetByteCount(item.ToJsonString(EngineJson.Default.Options)) > 32 * 1024 && item["answer"] is not null)
            {
                item["answer"] = null; item["answerText"] = null; item["answerComplete"] = false;
            }
            if (prefix.LongLength != total || Encoding.UTF8.GetByteCount(item.ToJsonString(EngineJson.Default.Options)) > 32 * 1024)
            {
                item["text"] = Scalars(text, 256); item["textComplete"] = false; item["textUtf8Bytes"] = total;
            }
            if (Encoding.UTF8.GetByteCount(item.ToJsonString(EngineJson.Default.Options)) > 32 * 1024)
                foreach (var field in new[] { "title", "recommendation", "inputMode", "schema", "options", "freeText", "optionsJson" }) item[field] = null;
        }
        else
        {
            var labelBytes = row.IsNull(jsonColumn + 3) ? [] : row.Blob(jsonColumn + 3);
            var labelTotal = row.IsNull(jsonColumn + 4) ? 0 : row.Long(jsonColumn + 4);
            var label = Utf8Prefix(labelBytes, labelTotal);
            var labelPreview = Scalars(label, 256);
            item["senderLabel"] = labelPreview;
            if (labelBytes.LongLength != labelTotal || labelPreview != label)
            { item["senderLabelComplete"] = false; item["senderLabelUtf8Bytes"] = labelTotal; }
            item["text"] = text;
            item["textComplete"] = prefix.LongLength == total ? 1 : 0;
            if (prefix.LongLength != total || Encoding.UTF8.GetByteCount(item.ToJsonString(EngineJson.Default.Options)) > 32 * 1024)
            { item["text"] = Scalars(text, 256); item["textComplete"] = 0; }
        }
        return Parse(item.ToJsonString(EngineJson.Default.Options));
    }

    internal static string Utf8Prefix(byte[] prefix, long total)
    {
        var length = prefix.Length;
        if (length < total)
        {
            // Drop only an incomplete final scalar. Invalid complete input is refused by strict decoding.
            var start = length - 1;
            while (start >= 0 && (prefix[start] & 0xc0) == 0x80) start--;
            if (start >= 0)
            {
                var lead = prefix[start];
                var needed = lead < 0x80 ? 1 : lead is >= 0xc2 and <= 0xdf ? 2 : lead is >= 0xe0 and <= 0xef ? 3 : lead is >= 0xf0 and <= 0xf4 ? 4 : 1;
                if (length - start < needed) length = start;
            }
        }
        return new UTF8Encoding(false, true).GetString(prefix.AsSpan(0, length));
    }

    internal static string Scalars(string value, int limit) => string.Concat(value.EnumerateRunes().Take(limit).Select(r => r.ToString()));

    private static string CamelCase(string column)
    {
        var parts = column.Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    internal static string[] Kinds(string topic) => topic switch
    {
        "mirror" => ["session", "run", "step", "needs_me", "message", "handoff", "pr", "pr_observation", "provider", "sign_in"],
        "runs" => ["run", "step", "handoff"],
        "sessions" => ["session", "handoff"],
        "needs_me" => ["needs_me"],
        "messages" => ["message"],
        "prs" => ["pr", "pr_observation"],
        "usage" => ["usage", "tool_call"],
        "providers" => ["provider", "sign_in"],
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
