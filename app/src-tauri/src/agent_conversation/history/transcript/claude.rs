use serde_json::{json, Value};
use std::collections::BTreeMap;
use std::fs;
use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};

use super::{
    home_dir, object, parse_json_lines, read_snapshot_text, stable_key, timestamp,
    ProjectedRecord,
};
use crate::agent_conversation::protocol::{AgentConversationPayload, AgentEventType};

pub(super) fn discover_path(id: &str) -> Result<Option<PathBuf>, String> {
    find(&home_dir()?.join(".claude/projects"), id)
}

/// Claude Code lists each live run, interactive or `-p`, as `~/.claude/sessions/<pid>.json` naming its session, and removes
/// it on exit; a run that was killed leaves one naming a process that is gone.
pub(super) fn running(home: &Path, id: &str) -> bool {
    let records = fs::read_dir(home.join(".claude/sessions")).into_iter().flatten().flatten();
    records.filter_map(|entry| serde_json::from_str::<Value>(&fs::read_to_string(entry.path()).ok()?).ok())
        .any(|run| run["sessionId"] == id && run["pid"].as_i64().is_some_and(|pid| unsafe { libc::kill(pid as libc::pid_t, 0) } == 0))
}

pub(super) fn discover_child_path(
    parent_id: &str,
    child_id: &str,
) -> Result<Option<PathBuf>, String> {
    let Some(parent) = discover_path(parent_id)? else {
        return Ok(None);
    };
    find_child(&parent, parent_id, child_id)
}

pub(super) fn child_watch_directory(parent_id: &str) -> Result<Option<PathBuf>, String> {
    Ok(discover_path(parent_id)?.and_then(|path| path.parent().map(Path::to_path_buf)))
}

/// Whether the transcript Claude keeps for this session holds a turn.
///
/// The Claude CLI writes a session's file the moment it starts — a queue line,
/// the title — and the turn itself some fifty milliseconds after answering it.
/// A file with the first and not the second is one whose process was stopped
/// before it wrote, and it cannot be resumed: the CLI reads a session holding
/// no conversation and leaves. A file that cannot be found holds nothing either.
pub(super) fn holds_a_turn(id: &str) -> Result<bool, String> {
    match discover_path(id)? {
        Some(path) => holds_a_turn_at(&path),
        None => Ok(false),
    }
}

fn holds_a_turn_at(path: &Path) -> Result<bool, String> {
    let file = fs::File::open(path).map_err(|error| error.to_string())?;
    for line in BufReader::new(file).lines() {
        let line = line.map_err(|error| error.to_string())?;
        let is_turn = serde_json::from_str::<Value>(&line)
            .ok()
            .and_then(|record| record.get("type")?.as_str().map(str::to_owned))
            .is_some_and(|kind| kind == "user" || kind == "assistant");
        if is_turn {
            return Ok(true);
        }
    }
    Ok(false)
}

fn find(root: &Path, id: &str) -> Result<Option<PathBuf>, String> {
    let filename = format!("{id}.jsonl");
    let mut pending = vec![root.to_path_buf()];
    while let Some(directory) = pending.pop() {
        let Ok(entries) = fs::read_dir(directory) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_dir() {
                pending.push(path);
            } else if path.file_name().and_then(|name| name.to_str()) == Some(filename.as_str()) {
                return Ok(Some(path));
            }
        }
    }
    Ok(None)
}

/// Removes the parts of a user turn that the user did not write.
///
/// Several kinds of machine-authored text are recorded under the person's own
/// role: a sub-agent reporting that it finished, the reminders the harness
/// injects, the output of a slash command, and the summary that opens a session
/// continued after a compaction. Imported as messages they read as though the
/// person said them. One real transcript held three typed messages and thirteen
/// of these, and the conversation it produced looked like somebody else's work.
///
/// The blocks are cut out rather than the whole turn dropped, because a
/// reminder is often appended to something the person really did type. A turn
/// left empty afterwards was entirely machine-written and is not stored at all.
/// The continuation summary is the exception: it is a whole synthetic turn, so
/// it goes as one.
fn strip_injected(text: &str) -> String {
    if text.starts_with("This session is being continued from a previous conversation") {
        return String::new();
    }
    let mut cleaned = text.to_string();
    for (open, close) in [
        ("<task-notification>", "</task-notification>"),
        ("<system-reminder>", "</system-reminder>"),
        ("<local-command-stdout>", "</local-command-stdout>"),
    ] {
        while let Some(start) = cleaned.find(open) {
            let Some(end) = cleaned[start..]
                .find(close)
                .map(|at| start + at + close.len())
            else {
                break;
            };
            cleaned.replace_range(start..end, "");
        }
    }
    cleaned.trim().to_string()
}

pub(super) fn project(value: &Value, line: &[u8], session_id: &str) -> Vec<ProjectedRecord> {
    if value.get("sessionId").and_then(Value::as_str) != Some(session_id)
        || value.get("isMeta").and_then(Value::as_bool) == Some(true)
        || value.get("isSidechain").and_then(Value::as_bool) == Some(true)
    {
        return Vec::new();
    }
    // The point where the older part of the conversation was thrown away. It
    // is written under its own record type rather than as a turn, so it has to
    // be read before the filter below, which keeps only what somebody said.
    if value.get("type").and_then(Value::as_str) == Some("system")
        && value.get("subtype").and_then(Value::as_str) == Some("compact_boundary")
    {
        let trigger = value
            .pointer("/compactMetadata/trigger")
            .and_then(Value::as_str)
            .map(str::to_string);
        let pre_tokens = value
            .pointer("/compactMetadata/preTokens")
            .and_then(Value::as_u64);
        let post_tokens = value
            .pointer("/compactMetadata/postTokens")
            .and_then(Value::as_u64);
        return vec![ProjectedRecord {
            key: stable_key("claude-compaction", line),
            event_type: AgentEventType::ItemCompleted,
            timestamp_ms: timestamp(value),
            item_id: None,
            payload: BTreeMap::from([
                ("kind".into(), json!("contextCompaction")),
                ("historical".into(), json!(true)),
                ("trigger".into(), json!(trigger)),
                ("preTokens".into(), json!(pre_tokens)),
                ("postTokens".into(), json!(post_tokens)),
            ]),
            native: Some(AgentConversationPayload::ContextCompaction {
                trigger,
                pre_tokens,
                post_tokens,
            }),
        }];
    }
    let Some(role) = value
        .get("type")
        .and_then(Value::as_str)
        .filter(|role| *role == "user" || *role == "assistant")
    else {
        return Vec::new();
    };
    let text = match value.pointer("/message/content") {
        Some(Value::String(text)) if role == "user" => text.trim().to_string(),
        Some(Value::Array(items)) => items
            .iter()
            .filter(|item| item.get("type").and_then(Value::as_str) == Some("text"))
            .filter_map(|item| item.get("text").and_then(Value::as_str))
            .map(str::trim)
            .filter(|text| !text.is_empty())
            .collect::<Vec<_>>()
            .join("\n\n"),
        _ => String::new(),
    };
    let text = if role == "user" {
        strip_injected(&text)
    } else {
        text
    };
    let mut records = Vec::new();
    if !text.is_empty() {
        let item_id = value
            .get("uuid")
            .and_then(Value::as_str)
            .or_else(|| value.pointer("/message/id").and_then(Value::as_str))
            .map(str::to_string)
            .unwrap_or_else(|| stable_key("claude-message", line));
        let item_type = if role == "user" {
            "user-message"
        } else {
            "assistant-message"
        };
        let item = object([
            ("id", json!(item_id)),
            ("type", json!(item_type)),
            ("content", json!([{"channel": "assistant", "text": text}])),
            (
                "providerMetadata",
                json!({"historical": true, "transcriptRole": role}),
            ),
        ]);
        // The same message, said the way the app says it. This is what gets
        // stored; the item above is the older wrapped form.
        let native = if role == "user" {
            AgentConversationPayload::UserMessage {
                item_id: item_id.clone(),
                text: text.clone(),
                completed: true,
                attachment_ids: Vec::new(),
            }
        } else {
            AgentConversationPayload::AssistantMessage {
                item_id: item_id.clone(),
                text: text.clone(),
                completed: true,
                blocks: None,
            }
        };
        records.push(ProjectedRecord {
            key: format!("item:{item_id}"),
            event_type: AgentEventType::ItemCompleted,
            timestamp_ms: timestamp(value),
            item_id: Some(item_id),
            payload: BTreeMap::from([("historical".into(), json!(true)), ("item".into(), item)]),
            native: Some(native),
        });
    }
    // The tools the reply used, in the order it used them. They sit alongside
    // the text in the same message, which is why they are read from the same
    // line: a reply and the work it did are one turn, not two. The result of a
    // call is recorded separately, under the person's role and with no tool name
    // on it, so it is read past rather than turned into a row of its own.
    if role == "assistant" {
        for block in value
            .pointer("/message/content")
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
            .filter(|block| block.get("type").and_then(Value::as_str) == Some("tool_use"))
        {
            let Some(item_id) = block.get("id").and_then(Value::as_str) else {
                continue;
            };
            let tool_name = block.get("name").and_then(Value::as_str).unwrap_or("Tool");
            let input = block.get("input").unwrap_or(&Value::Null);
            records.push(super::tool_record(
                item_id,
                tool_name,
                input,
                timestamp(value),
            ));
        }
    }
    // The answer to a call is written on the person's turn rather than the
    // assistant's, under the id of the call it answers and with no tool name on
    // it. It used to be read past entirely, which is why a tool row had nothing
    // to open: what the tool was asked was kept and what it said was dropped.
    if role == "user" {
        for block in value
            .pointer("/message/content")
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
            .filter(|block| block.get("type").and_then(Value::as_str) == Some("tool_result"))
        {
            let Some(item_id) = block.get("tool_use_id").and_then(Value::as_str) else {
                continue;
            };
            let Some(output) = block.get("content").and_then(super::tool_output_text) else {
                continue;
            };
            records.push(super::tool_output_record(item_id, output, timestamp(value)));
        }
    }
    let model = value.pointer("/message/model").and_then(Value::as_str);
    let effort = value.get("effort").and_then(Value::as_str);
    let approval = value.get("permissionMode").and_then(Value::as_str);
    if model.is_some() || effort.is_some() || approval.is_some() {
        records.push(ProjectedRecord {
            key: stable_key("claude-config", line),
            event_type: AgentEventType::SessionConfigUpdated,
            timestamp_ms: timestamp(value),
            item_id: None,
            payload: BTreeMap::from([
                ("historical".into(), json!(true)),
                (
                    "config".into(),
                    json!({"model": model, "effort": effort, "approvalPolicy": approval}),
                ),
            ]),
            // The app has no event of its own for a past session's settings.
            native: None,
        });
    }
    if let Some(usage) = value.pointer("/message/usage") {
        let used = [
            "input_tokens",
            "cache_creation_input_tokens",
            "cache_read_input_tokens",
        ]
        .into_iter()
        .filter_map(|key| usage.get(key).and_then(Value::as_u64))
        .sum::<u64>();
        if used > 0 {
            records.push(ProjectedRecord {
                key: stable_key("claude-usage", line),
                event_type: AgentEventType::UsageUpdated,
                timestamp_ms: timestamp(value),
                item_id: None,
                payload: BTreeMap::from([
                    ("historical".into(), json!(true)),
                    ("usedTokens".into(), json!(used)),
                ]),
                native: Some(AgentConversationPayload::Usage {
                    input_tokens: None,
                    output_tokens: None,
                    used_tokens: Some(used),
                    context_window: None,
                    total_tokens: None,
                }),
            });
        }
    }
    records
}

pub(super) fn project_child(
    value: &Value,
    line: &[u8],
    parent_id: &str,
    child_id: &str,
) -> Vec<ProjectedRecord> {
    if value.get("sessionId").and_then(Value::as_str) != Some(parent_id)
        || value.get("agentId").and_then(Value::as_str) != Some(child_id)
        || value.get("isSidechain").and_then(Value::as_bool) != Some(true)
    {
        return Vec::new();
    }
    let mut projected = value.clone();
    projected["isSidechain"] = Value::Bool(false);
    project(&projected, line, parent_id)
}

fn children_dir(parent: &Path, id: &str) -> Option<PathBuf> {
    Some(parent.parent()?.join(id).join("subagents"))
}
fn find_child(parent: &Path, id: &str, child_id: &str) -> Result<Option<PathBuf>, String> {
    let Some(dir) = children_dir(parent, id) else {
        return Ok(None);
    };
    let Ok(entries) = fs::read_dir(dir) else {
        return Ok(None);
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.extension().and_then(|value| value.to_str()) != Some("jsonl") {
            continue;
        }
        let (input, _) = read_snapshot_text(&path)?;
        if parse_json_lines(&input).any(|value| {
            value.get("sessionId").and_then(Value::as_str) == Some(id)
                && value.get("agentId").and_then(Value::as_str) == Some(child_id)
                && value.get("isSidechain").and_then(Value::as_bool) == Some(true)
        })
        {
            return Ok(Some(path));
        }
    }
    Ok(None)
}
#[cfg(test)]
mod tests {
    use super::holds_a_turn_at;
    use std::path::PathBuf;

    #[test]
    fn a_session_claude_is_running_is_found_by_its_live_record() {
        let home = std::env::temp_dir().join(format!("claude-running-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(home.join(".claude/sessions")).unwrap();
        let record = |pid: u32| format!(r#"{{"pid":{pid},"sessionId":"S1","kind":"interactive"}}"#);
        std::fs::write(home.join(".claude/sessions/1.json"), record(std::process::id())).unwrap();
        assert!(super::running(&home, "S1"));
        assert!(!super::running(&home, "S2"));
        // A record left by a run that was killed names a process that is gone.
        let mut gone = std::process::Command::new("true").spawn().unwrap();
        let pid = gone.id();
        gone.wait().unwrap();
        std::fs::write(home.join(".claude/sessions/1.json"), record(pid)).unwrap();
        assert!(!super::running(&home, "S1"));
        std::fs::remove_dir_all(home).unwrap();
    }

    fn transcript(name: &str, lines: &[&str]) -> PathBuf {
        let path = std::env::temp_dir().join(format!(
            "mcb-holds-a-turn-{}-{name}.jsonl",
            std::process::id()
        ));
        std::fs::write(&path, lines.join("\n") + "\n").unwrap();
        path
    }

    // Stopped before it wrote: the queue line is there and the turn is not.
    #[test]
    fn a_transcript_with_only_the_queue_line_holds_no_turn() {
        let path = transcript(
            "queue-only",
            &[r#"{"type":"queue-operation","operation":"dequeue","sessionId":"s"}"#],
        );
        assert!(!holds_a_turn_at(&path).unwrap());
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn a_transcript_with_a_user_message_holds_a_turn() {
        let path = transcript(
            "with-turn",
            &[
                r#"{"type":"ai-title","aiTitle":"Hello","sessionId":"s"}"#,
                r#"{"type":"queue-operation","operation":"dequeue","sessionId":"s"}"#,
                r#"{"type":"user","message":{"role":"user","content":"hello"},"sessionId":"s"}"#,
            ],
        );
        assert!(holds_a_turn_at(&path).unwrap());
        let _ = std::fs::remove_file(path);
    }
}
