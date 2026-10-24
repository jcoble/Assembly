use chrono::{DateTime, Datelike, Local};
use serde_json::{json, Value};
use std::collections::BTreeMap;
use std::fs;
use std::fs::File;
use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::time::SystemTime;

use super::{
    home_dir, object, stable_key, timestamp,
    CodexChildRollout, ProjectedRecord,
};
use crate::agent_conversation::protocol::{AgentConversationPayload, AgentEventType};

pub(super) fn discover_path(id: &str) -> Option<PathBuf> {
    let home = home_dir().ok()?;
    [
        home.join(".codex/sessions"),
        home.join(".codex/archived_sessions"),
    ]
    .into_iter()
    .find_map(|root| find(&root, id).ok().flatten())
}

/// A missing rollout cannot resume; an I/O error leaves its existence unknown.
pub(crate) fn holds_session(id: &str) -> Result<bool, String> {
    holds_session_at(&home_dir()?, &super::safe_session_id(id)?)
}

pub(super) fn holds_session_at(home: &Path, id: &str) -> Result<bool, String> {
    for root in [home.join(".codex/sessions"), home.join(".codex/archived_sessions")] {
        if find(&root, id)?.is_some() {
            return Ok(true);
        }
    }
    Ok(false)
}

pub(super) fn discover_child_path(parent_id: &str, child_id: &str) -> Option<PathBuf> {
    let path = discover_path(child_id)?;
    child_rollout_from_first_line(&path, parent_id)
        .filter(|child| child.child_id == child_id)
        .map(|_| path)
}

pub(super) fn child_watch_directory(parent_id: &str, child_id: &str) -> Result<PathBuf, String> {
    if let Some(path) = discover_child_path(parent_id, child_id) {
        return path
            .parent()
            .map(Path::to_path_buf)
            .ok_or_else(|| "Child transcript has no containing directory".to_string());
    }
    Ok(super::home_dir()?.join(".codex/sessions"))
}

pub(super) fn scan_child_rollouts(
    parent_path: &Path,
    parent_id: &str,
) -> Result<Vec<CodexChildRollout>, String> {
    let parent_directory = parent_path
        .parent()
        .ok_or_else(|| "The parent transcript has no dated directory".to_string())?;
    let sessions_root = parent_directory
        .ancestors()
        .nth(3)
        .ok_or_else(|| "The parent transcript has no sessions directory".to_string())?;
    let today: DateTime<Local> = SystemTime::now().into();
    let today_directory = sessions_root
        .join(format!("{:04}", today.year()))
        .join(format!("{:02}", today.month()))
        .join(format!("{:02}", today.day()));

    // A parent may live in an older date directory while children spawned now
    // are written under today's date, so scan both when those directories differ.
    let mut directories = vec![parent_directory.to_path_buf()];
    if today_directory != parent_directory {
        directories.push(today_directory);
    }
    let mut children = BTreeMap::new();
    for directory in directories {
        let entries = match fs::read_dir(directory) {
            Ok(entries) => entries,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => continue,
            Err(error) => return Err(format!("Could not scan child transcripts: {error}")),
        };
        for child in entries.flatten().filter_map(|entry| {
            child_rollout_from_first_line(&entry.path(), parent_id)
        }) {
            children.insert(child.child_id.clone(), child);
        }
    }
    Ok(children.into_values().collect())
}

fn child_rollout_from_first_line(
    path: &Path,
    parent_id: &str,
) -> Option<CodexChildRollout> {
    if path.extension().and_then(|value| value.to_str()) != Some("jsonl") {
        return None;
    }
    let mut first_line = String::new();
    BufReader::new(File::open(path).ok()?)
        .read_line(&mut first_line)
        .ok()?;
    let value: Value = serde_json::from_str(&first_line).ok()?;
    let payload = value
        .get("type")
        .and_then(Value::as_str)
        .filter(|kind| *kind == "session_meta")
        .and_then(|_| value.get("payload"))?;
    if payload.get("parent_thread_id").and_then(Value::as_str) != Some(parent_id)
        || payload.get("thread_source").and_then(Value::as_str) != Some("subagent")
    {
        return None;
    }
    let child_id = payload.get("id").and_then(Value::as_str)?.to_string();
    let spawn = payload.pointer("/source/subagent/thread_spawn");
    let role = spawn
        .and_then(|value| value.get("agent_role"))
        .or_else(|| payload.get("agent_role"))
        .and_then(Value::as_str);
    let name = spawn
        .and_then(|value| value.get("agent_path"))
        .or_else(|| payload.get("agent_path"))
        .and_then(Value::as_str)
        .and_then(|path| path.rsplit('/').find(|part| !part.is_empty()))
        .or_else(|| {
            spawn
                .and_then(|value| value.get("agent_nickname"))
                .or_else(|| payload.get("agent_nickname"))
                .and_then(Value::as_str)
        })
        .unwrap_or("Sub-agent");
    let label = role.map_or_else(|| name.to_string(), |role| format!("{name} ({role})"));
    let running = !last_turn_ended(path) && !silent_too_long(path);
    Some(CodexChildRollout {
        child_id,
        label,
        state: if running { "running" } else { "finished" }.to_string(),
        latest_activity: if running { "Running" } else { "Finished" }.to_string(),
    })
}

/// How long a child may write nothing before it counts as gone. A child that crashed or was
/// killed never writes its closing record; without this its session could never suspend.
pub(crate) const CHILD_SILENCE_LIMIT: std::time::Duration = std::time::Duration::from_secs(60 * 60);

fn silent_too_long(path: &Path) -> bool {
    path.metadata().and_then(|metadata| metadata.modified()).ok()
        .and_then(|modified| modified.elapsed().ok())
        .is_some_and(|silence| silence > CHILD_SILENCE_LIMIT)
}

/// How much of a child transcript's end is read to find its newest turn record.
const TURN_RECORD_TAIL_BYTES: u64 = 64 * 1024;

/// Whether the transcript's newest turn record says the turn ended (`task_complete` or
/// `turn_aborted`) rather than started. A short quiet time proves nothing: a long command
/// writes nothing until it returns. Only the end of the file is read.
fn last_turn_ended(path: &Path) -> bool {
    let Ok(len) = path.metadata().map(|metadata| metadata.len()) else { return false };
    let start = len.saturating_sub(TURN_RECORD_TAIL_BYTES);
    let Ok(bytes) = super::read_range(path, start, TURN_RECORD_TAIL_BYTES) else { return false };
    let (lines, _) = super::complete_lines(&bytes, start > 0);
    lines.iter().rev().find_map(|line| {
        let value: Value = serde_json::from_slice(line).ok()?;
        if value.get("type").and_then(Value::as_str) != Some("event_msg") {
            return None;
        }
        match value.pointer("/payload/type").and_then(Value::as_str)? {
            "task_complete" | "turn_aborted" => Some(true),
            "task_started" => Some(false),
            _ => None,
        }
    }).unwrap_or(false)
}

/// Codex holds an exclusive lock on `~/.codex/thread-writer-locks/<thread id>.lock` while it writes the thread, the app and
/// the CLI alike, so a lock taken at once means nothing is running it.
pub(super) fn running(home: &Path, id: &str) -> bool {
    use std::os::fd::AsRawFd;
    File::open(home.join(".codex/thread-writer-locks").join(format!("{id}.lock")))
        .is_ok_and(|lock| unsafe { libc::flock(lock.as_raw_fd(), libc::LOCK_SH | libc::LOCK_NB) } != 0)
}

fn find(root: &Path, id: &str) -> Result<Option<PathBuf>, String> {
    let suffix = format!("{id}.jsonl");
    let mut pending = vec![root.to_path_buf()];
    let mut unreadable = None;
    while let Some(directory) = pending.pop() {
        let entries = match fs::read_dir(&directory) {
            Ok(entries) => entries,
            Err(error) if directory == root && error.kind() == std::io::ErrorKind::NotFound => continue,
            Err(error) => { unreadable.get_or_insert(error.to_string()); continue; }
        };
        for entry in entries {
            let entry = match entry {
                Ok(entry) => entry,
                Err(error) => { unreadable.get_or_insert(error.to_string()); continue; }
            };
            let path = entry.path();
            let metadata = match path.metadata() {
                Ok(metadata) => metadata,
                Err(error) => { unreadable.get_or_insert(error.to_string()); continue; }
            };
            if metadata.is_dir() {
                pending.push(path);
            } else if path
                .file_name()
                .and_then(|name| name.to_str())
                .is_some_and(|name| name.ends_with(&suffix))
            {
                if let Err(error) = File::open(&path) {
                    unreadable.get_or_insert(error.to_string());
                    continue;
                }
                return Ok(Some(path));
            }
        }
    }
    unreadable.map_or(Ok(None), Err)
}

/// The context Codex hands the model at the top of a thread, removed.
///
/// Codex opens a session by injecting what the person never typed — the
/// repository's AGENTS.md, the list of plugins it could install, the working
/// folder and date, and its own goal notes — and each one arrives wearing the
/// `user` role. Measured on this machine, the AGENTS.md message ran 29,444
/// characters and the plugin list 32,969, so an imported conversation opened
/// with tens of kilobytes of machinery ahead of the first real sentence.
///
/// A message that is nothing but injected context becomes empty and is dropped
/// by the caller. `<environment_context>` is cut rather than dropped whole
/// because Codex appends it to messages that also carry other text.
fn strip_injected(text: &str) -> String {
    const INJECTED_OPENINGS: &[&str] = &[
        "# AGENTS.md instructions",
        "<recommended_plugins>",
        "<codex_internal_context",
    ];
    if INJECTED_OPENINGS
        .iter()
        .any(|opening| text.starts_with(opening))
    {
        return String::new();
    }

    const OPEN: &str = "<environment_context>";
    const CLOSE: &str = "</environment_context>";
    let mut cleaned = text.to_string();
    while let Some(start) = cleaned.find(OPEN) {
        let Some(end) = cleaned[start..]
            .find(CLOSE)
            .map(|at| start + at + CLOSE.len())
        else {
            break;
        };
        cleaned.replace_range(start..end, "");
    }
    cleaned.trim().to_string()
}

pub(super) fn project(value: &Value, line: &[u8]) -> Vec<ProjectedRecord> {
    let mut records = Vec::new();
    match (
        value.get("type").and_then(Value::as_str),
        value.pointer("/payload/type").and_then(Value::as_str),
    ) {
        (Some("response_item"), Some("message")) => {
            let Some(role) = value
                .pointer("/payload/role")
                .and_then(Value::as_str)
                .filter(|role| *role == "user" || *role == "assistant")
            else {
                return records;
            };
            let text = value
                .pointer("/payload/content")
                .and_then(Value::as_array)
                .into_iter()
                .flatten()
                .filter_map(|item| {
                    let kind = item.get("type").and_then(Value::as_str)?;
                    ((role == "user" && kind == "input_text")
                        || (role == "assistant" && kind == "output_text"))
                        .then(|| item.get("text").and_then(Value::as_str))
                        .flatten()
                })
                .map(str::trim)
                .filter(|text| !text.is_empty())
                .collect::<Vec<_>>()
                .join("\n\n");
            let text = if role == "user" {
                strip_injected(&text)
            } else {
                text
            };
            if text.is_empty() {
                return records;
            }
            let item_id = value
                .pointer("/payload/id")
                .and_then(Value::as_str)
                .map(str::to_string)
                .unwrap_or_else(|| stable_key("codex-message", line));
            let item_type = if role == "user" {
                "user-message"
            } else {
                "assistant-message"
            };
            let item = object([
                ("id", json!(item_id)),
                ("type", json!(item_type)),
                (
                    "content",
                    json!([{"channel": if role == "user" { "assistant" } else { "assistant" }, "text": text}]),
                ),
                (
                    "providerMetadata",
                    json!({"historical": true, "transcriptRole": role}),
                ),
            ]);
            // The same message, said the way the app says it. This is what gets
            // stored; the item above is the older wrapped form, kept for the one
            // reader that still asks for it.
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
                payload: BTreeMap::from([
                    ("historical".into(), json!(true)),
                    ("item".into(), item),
                ]),
                native: Some(native),
            });
        }
        (Some("response_item"), Some("function_call_output" | "custom_tool_call_output")) => {
            // The result line carries the same `call_id` as the call and no
            // tool name, so it is the same row saying what came back. It used
            // to be read past, which left every tool row with nothing in it.
            let Some(call_id) = value.pointer("/payload/call_id").and_then(Value::as_str) else {
                return records;
            };
            let Some(output) = value
                .pointer("/payload/output")
                .and_then(super::tool_output_text)
            else {
                return records;
            };
            records.push(super::tool_output_record(call_id, output, timestamp(value)));
        }
        (Some("response_item"), Some("function_call" | "custom_tool_call")) => {
            // The call, not its result. Both lines carry the same `call_id`, but
            // only this one names the tool, and the app's tool event has one
            // field for what a call was — so a row is written from the call and
            // the result line is read past.
            let Some(call_id) = value.pointer("/payload/call_id").and_then(Value::as_str) else {
                return records;
            };
            let name = value
                .pointer("/payload/name")
                .and_then(Value::as_str)
                .unwrap_or("Tool");
            // Codex writes a call's arguments as a string: JSON for a plain
            // function call, a short script for a custom one. Decoded when it is
            // JSON so the row can show the command rather than the envelope.
            let arguments = value
                .pointer("/payload/arguments")
                .or_else(|| value.pointer("/payload/input"))
                .and_then(Value::as_str)
                .map(|raw| serde_json::from_str::<Value>(raw).unwrap_or_else(|_| json!(raw)))
                .unwrap_or(Value::Null);
            records.push(super::tool_record(
                call_id,
                name,
                &arguments,
                timestamp(value),
            ));
        }
        (Some("turn_context"), _) => {
            let config = json!({
                "model": value.pointer("/payload/model").and_then(Value::as_str),
                "effort": value.pointer("/payload/effort").and_then(Value::as_str),
                "approvalPolicy": value.pointer("/payload/approval_policy").and_then(Value::as_str)
            });
            records.push(ProjectedRecord {
                key: stable_key("codex-config", line),
                event_type: AgentEventType::SessionConfigUpdated,
                timestamp_ms: timestamp(value),
                item_id: None,
                payload: BTreeMap::from([
                    ("historical".into(), json!(true)),
                    ("config".into(), config),
                ]),
                // The app has no event of its own for a past session's settings,
                // so this one stays wrapped. Only the newest is kept anyway.
                native: None,
            });
        }
        (Some("event_msg"), Some("token_count")) => {
            let used_tokens = value
                .pointer("/payload/info/last_token_usage/total_tokens")
                .and_then(Value::as_u64);
            let total_tokens = value
                .pointer("/payload/info/total_token_usage/total_tokens")
                .and_then(Value::as_u64);
            let context_window = value
                .pointer("/payload/info/model_context_window")
                .and_then(Value::as_u64);
            records.push(ProjectedRecord {
                key: stable_key("codex-usage", line),
                event_type: AgentEventType::UsageUpdated,
                timestamp_ms: timestamp(value),
                item_id: None,
                native: Some(AgentConversationPayload::Usage {
                    input_tokens: None,
                    output_tokens: None,
                    used_tokens,
                    context_window,
                    total_tokens,
                }),
                payload: BTreeMap::from([
                    ("historical".into(), json!(true)),
                    (
                        "usedTokens".into(),
                        value
                            .pointer("/payload/info/last_token_usage/total_tokens")
                            .cloned()
                            .unwrap_or(Value::Null),
                    ),
                    (
                        "totalTokens".into(),
                        value
                            .pointer("/payload/info/total_token_usage/total_tokens")
                            .cloned()
                            .unwrap_or(Value::Null),
                    ),
                    (
                        "contextWindow".into(),
                        value
                            .pointer("/payload/info/model_context_window")
                            .cloned()
                            .unwrap_or(Value::Null),
                    ),
                ]),
            });
        }
        (Some("event_msg"), Some("context_compacted")) => {
            // The rollout records only that it happened, so the row says only
            // that. What it cost is not written at this layer.
            records.push(ProjectedRecord {
                key: stable_key("codex-compaction", line),
                event_type: AgentEventType::ItemCompleted,
                timestamp_ms: timestamp(value),
                item_id: None,
                payload: BTreeMap::from([
                    ("kind".into(), json!("contextCompaction")),
                    ("historical".into(), json!(true)),
                ]),
                native: Some(AgentConversationPayload::ContextCompaction {
                    trigger: None,
                    pre_tokens: None,
                    post_tokens: None,
                }),
            });
        }
        (Some("event_msg"), Some("sub_agent_activity")) => {
            let Some(child_id) = value
                .pointer("/payload/agent_thread_id")
                .and_then(Value::as_str)
            else {
                return records;
            };
            let kind = value
                .pointer("/payload/kind")
                .and_then(Value::as_str)
                .unwrap_or("unknown");
            let state = if matches!(kind, "completed" | "finished") {
                "completed"
            } else if matches!(kind, "failed" | "errored" | "interrupted") {
                "failed"
            } else {
                "historical"
            };
            let child = json!({"childId": child_id, "label": value.pointer("/payload/agent_path").and_then(Value::as_str).unwrap_or("Sub-agent"), "state": state, "historical": true});
            records.push(ProjectedRecord {
                key: stable_key("codex-child", line),
                event_type: AgentEventType::ChildrenUpdated,
                timestamp_ms: timestamp(value),
                item_id: None,
                payload: BTreeMap::from([
                    ("historical".into(), json!(true)),
                    ("children".into(), json!([child])),
                ]),
                // A live child update names the tool call that started it, and a
                // transcript does not record that, so there is nothing faithful
                // to convert this into. It stays wrapped rather than inventing
                // an id that points at nothing.
                native: None,
            });
        }
        _ => {}
    }
    records
}

#[cfg(test)]
mod running_tests {
    use std::os::fd::AsRawFd;

    #[test]
    fn a_thread_codex_is_writing_is_found_by_its_lock() {
        let home = std::env::temp_dir().join(format!("codex-running-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(home.join(".codex/thread-writer-locks")).unwrap();
        let lock = std::fs::File::create(home.join(".codex/thread-writer-locks/T1.lock")).unwrap();
        assert!(!super::running(&home, "T1"), "a lock file nobody holds is left over");
        assert_eq!(unsafe { libc::flock(lock.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) }, 0);
        assert!(super::running(&home, "T1"));
        assert!(!super::running(&home, "T2"));
        std::fs::remove_dir_all(home).unwrap();
    }
}
