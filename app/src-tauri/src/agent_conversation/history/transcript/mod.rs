mod claude;
mod codex;
pub(crate) use codex::holds_session as codex_transcript_holds_session;
use serde_json::{Map, Value};
use std::collections::hash_map::DefaultHasher;
use std::fs::{self, File, Metadata};
use std::hash::{Hash, Hasher};
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::time::SystemTime;

use super::super::protocol::{
    AgentConversationPayload, AgentConversationProvider, AgentEventType, ToolState,
};

pub const RECONCILIATION_BYTES: u64 = 4 * 1024 * 1024;

/// How much of a tool call's arguments one transcript row shows.
const TOOL_SUMMARY_CHARS: usize = 200;

#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct CodexChildRollout {
    pub child_id: String,
    pub label: String,
    pub state: String,
    pub latest_activity: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FileIdentity {
    pub device: u64,
    pub inode: u64,
}

#[derive(Clone, Debug)]
pub struct TranscriptLocation {
    pub canonical_path: PathBuf,
    pub identity: FileIdentity,
    pub len: u64,
}

#[derive(Clone, Debug)]
pub struct ProjectedRecord {
    pub key: String,
    pub event_type: AgentEventType,
    pub timestamp_ms: u128,
    pub item_id: Option<String>,
    pub payload: std::collections::BTreeMap<String, Value>,
    /// The record as the app's own event, when it has one.
    ///
    /// A message read out of a past transcript is a message: the same thing a
    /// live turn produces, and worth storing as the same thing. Records that say
    /// this keep nothing of the file they came from, which is the point — a
    /// resumed conversation and the turns taken after it stop being two shapes
    /// the reader has to tell apart.
    ///
    /// `None` means there is no equivalent and the record is stored wrapped, the
    /// way every imported record used to be. Only the session's configuration
    /// falls here now, and only one of those is kept per session.
    pub native: Option<AgentConversationPayload>,
}

pub fn parse_provider(value: &str) -> Result<AgentConversationProvider, String> {
    AgentConversationProvider::parse(value).ok_or_else(|| "Transcript provider is unsupported".to_string())
}

/// Whether the agent's own CLI or app is running this session now (two writers); ids are unique across agents, so both are asked.
pub fn running_elsewhere(id: &str) -> bool {
    home_dir().is_ok_and(|home| claude::running(&home, id) || codex::running(&home, id))
}

pub fn discover(provider: AgentConversationProvider, native_session_id: &str) -> Result<Option<TranscriptLocation>, String> {
    let id = safe_session_id(native_session_id)?;
    let path = match provider {
        AgentConversationProvider::Codex => codex::discover_path(&id),
        AgentConversationProvider::Claude => claude::discover_path(&id)?,
        _ => None, // No transcript reader for this provider (Antigravity: TSK-1297).
    };
    path.map(location).transpose()
}

pub fn discover_child(
    provider: AgentConversationProvider,
    parent_native_session_id: &str,
    child_session_id: &str,
) -> Result<Option<TranscriptLocation>, String> {
    let parent_id = safe_session_id(parent_native_session_id)?;
    let child_id = safe_session_id(child_session_id)?;
    let path = match provider {
        AgentConversationProvider::Codex => codex::discover_child_path(&parent_id, &child_id),
        AgentConversationProvider::Claude => claude::discover_child_path(&parent_id, &child_id)?,
        _ => None, // No transcript reader for this provider (Antigravity: TSK-1297).
    };
    path.map(location).transpose()
}

pub fn child_watch_directory(
    provider: AgentConversationProvider,
    parent_native_session_id: &str,
    child_session_id: &str,
) -> Result<PathBuf, String> {
    let parent_id = safe_session_id(parent_native_session_id)?;
    let child_id = safe_session_id(child_session_id)?;
    match provider {
        AgentConversationProvider::Codex => codex::child_watch_directory(&parent_id, &child_id),
        AgentConversationProvider::Claude => claude::child_watch_directory(&parent_id)?
            .ok_or_else(|| format!("Parent transcript {parent_id} was not found")),
        _ => { // No transcript reader for this provider (Antigravity: TSK-1297).
            Err(format!("{} has no transcript reader yet.", provider.descriptor().display_name))
        }
    }
}

/// Whether Claude's own transcript for this session holds a turn to resume.
pub fn claude_transcript_holds_a_turn(native_session_id: &str) -> Result<bool, String> {
    claude::holds_a_turn(&safe_session_id(native_session_id)?)
}

#[cfg(test)]
pub(crate) use codex::CHILD_SILENCE_LIMIT;

pub(crate) fn scan_codex_child_rollouts(
    parent_path: &Path,
    parent_id: &str,
) -> Result<Vec<CodexChildRollout>, String> {
    codex::scan_child_rollouts(parent_path, parent_id)
}

pub fn inspect(path: &Path) -> Result<Option<TranscriptLocation>, String> {
    if !path.is_file() {
        return Ok(None);
    }
    location(path.to_path_buf()).map(Some)
}

pub fn parse_durable_line(
    provider: AgentConversationProvider,
    native_session_id: &str,
    line: &[u8],
) -> Vec<ProjectedRecord> {
    let Ok(value) = serde_json::from_slice::<Value>(line) else {
        return Vec::new();
    };
    match provider {
        AgentConversationProvider::Codex => codex::project(&value, line),
        AgentConversationProvider::Claude => claude::project(&value, line, native_session_id),
        _ => Vec::new(), // No transcript reader for this provider (Antigravity: TSK-1297).
    }
}

pub fn parse_durable_child_line(
    provider: AgentConversationProvider,
    parent_native_session_id: &str,
    child_session_id: &str,
    line: &[u8],
) -> Vec<ProjectedRecord> {
    let Ok(value) = serde_json::from_slice::<Value>(line) else {
        return Vec::new();
    };
    match provider {
        AgentConversationProvider::Codex => codex::project(&value, line),
        AgentConversationProvider::Claude => {
            claude::project_child(&value, line, parent_native_session_id, child_session_id)
        }
        _ => Vec::new(), // No transcript reader for this provider (Antigravity: TSK-1297).
    }
}

pub fn read_range(path: &Path, start: u64, max_bytes: u64) -> Result<Vec<u8>, String> {
    let mut file =
        File::open(path).map_err(|error| format!("Could not open transcript: {error}"))?;
    file.seek(SeekFrom::Start(start))
        .map_err(|error| error.to_string())?;
    let mut bytes = Vec::new();
    file.take(max_bytes)
        .read_to_end(&mut bytes)
        .map_err(|error| error.to_string())?;
    Ok(bytes)
}

pub fn read_bounded(path: &Path, len: u64) -> Result<(u64, Vec<u8>), String> {
    let start = len.saturating_sub(RECONCILIATION_BYTES);
    Ok((start, read_range(path, start, RECONCILIATION_BYTES)?))
}

pub fn complete_lines(bytes: &[u8], skip_first_partial: bool) -> (Vec<&[u8]>, Vec<u8>) {
    let mut start = 0;
    if skip_first_partial {
        start = bytes
            .iter()
            .position(|byte| *byte == b'\n')
            .map_or(bytes.len(), |index| index + 1);
    }
    let mut lines = Vec::new();
    let mut search = start;
    while let Some(relative) = bytes[search..].iter().position(|byte| *byte == b'\n') {
        let end = search + relative;
        if end >= start {
            let line = bytes[start..end]
                .strip_suffix(b"\r")
                .unwrap_or(&bytes[start..end]);
            if !line.is_empty() {
                lines.push(line);
            }
            start = end + 1;
        }
        search = end + 1;
    }
    (lines, bytes[start..].to_vec())
}

pub fn safe_session_id(value: &str) -> Result<String, String> {
    let id = value.trim();
    if id.is_empty()
        || id.contains('/')
        || id.contains('\\')
        || !id
            .chars()
            .all(|ch| ch.is_ascii_alphanumeric() || ch == '-' || ch == '_')
    {
        return Err("Session id is invalid".to_string());
    }
    Ok(id.to_string())
}

pub fn home_dir() -> Result<PathBuf, String> {
    std::env::var_os("HOME").map(PathBuf::from).ok_or_else(|| "HOME is unavailable".to_string())
}

pub fn read_snapshot_text(path: &Path) -> Result<(String, bool), String> {
    let len = path.metadata().map_err(|error| error.to_string())?.len();
    let (start, bytes) = read_bounded(path, len)?;
    let (lines, _) = complete_lines(&bytes, start > 0);
    Ok((
        lines
            .into_iter()
            .map(String::from_utf8_lossy)
            .collect::<Vec<_>>()
            .join("\n"),
        start > 0,
    ))
}

pub fn parse_json_lines(input: &str) -> impl Iterator<Item = Value> + '_ {
    input
        .lines()
        .filter_map(|line| serde_json::from_str(line).ok())
}

pub fn object(entries: impl IntoIterator<Item = (impl Into<String>, Value)>) -> Value {
    Value::Object(
        entries
            .into_iter()
            .map(|(key, value)| (key.into(), value))
            .collect::<Map<_, _>>(),
    )
}

/// One line saying what a tool call was asked to do.
///
/// A call's arguments are the only thing a past transcript records about it,
/// so they are what the row shows. The named fields are read first because a
/// command or a path is the work itself, while the JSON around it is packaging;
/// anything else falls back to the arguments as written. Whitespace is
/// flattened so a row stays one row.
pub fn tool_summary(arguments: &Value) -> Option<String> {
    const NAMED: &[&str] = &[
        "cmd",
        "command",
        "CommandLine",
        "file_path",
        "path",
        "pattern",
        "query",
        "url",
        "Url",
        "description",
        "TargetFile",
        "AbsolutePath",
        "DirectoryPath",
        "SearchPath",
        "code",
    ];
    let text = NAMED
        .iter()
        .find_map(|key| arguments.get(*key).and_then(Value::as_str))
        .map(str::to_string)
        .unwrap_or_else(|| match arguments {
            Value::String(raw) => raw.clone(),
            Value::Null => String::new(),
            other => other.to_string(),
        });
    let flattened = text.split_whitespace().collect::<Vec<_>>().join(" ");
    (!flattened.is_empty()).then(|| flattened.chars().take(TOOL_SUMMARY_CHARS).collect())
}

/// A tool call read out of a past transcript, said the way the app says one.
///
/// It carries the app's own tool event rather than a description of a line in a
/// file, which is what lets a resumed conversation draw it with the same row a
/// live call gets instead of dropping it on the floor.
///
/// The state is always finished. A transcript is work that already happened,
/// and the record that would say a call failed — the result — is written under
/// a different id with no tool name on it, so there is nothing here to read a
/// failure from without inventing one.
pub(crate) fn simple_shell_file_path(command: &str) -> Option<String> {
    let command = command.trim().strip_prefix("$ ").unwrap_or(command.trim());
    let command = command
        .strip_prefix("/bin/bash -lc \"")
        .and_then(|script| script.strip_suffix('"'))
        .unwrap_or(command);
    // The observed ACP title wraps simple reads in bash and joins them with
    // `&&`. Skip other shell syntax rather than guessing from command output.
    if command.chars().any(|ch| ['\n', ';', '|', '>', '<', '$', '`'].contains(&ch)) {
        return None;
    }
    let paths: Vec<&str> = command
        .split(" && ")
        .filter_map(|part| {
            let words: Vec<&str> = part.split_whitespace().collect();
            match words.as_slice() {
                ["cat" | "head" | "tail", path] => Some(*path),
                ["sed", "-n", _, path] => Some(*path),
                _ => None,
            }
        })
        .map(|path| path.trim_matches(['\'', '"']))
        .filter(|path| !path.is_empty() && !path.starts_with('-'))
        .collect();
    (!paths.is_empty()).then(|| paths.join("\n"))
}

fn patch_file_paths(patch: &str) -> Option<String> {
    let paths: Vec<&str> = patch.lines().filter_map(|line| {
        ["*** Update File: ", "*** Add File: ", "*** Delete File: "]
            .iter()
            .find_map(|prefix| line.strip_prefix(prefix))
            .map(str::trim)
            .filter(|path| !path.is_empty())
    }).collect();
    (!paths.is_empty()).then(|| paths.join("\n"))
}

pub fn tool_record(
    item_id: &str,
    name: &str,
    input: &Value,
    timestamp_ms: u128,
) -> ProjectedRecord {
    let summary = tool_summary(input);
    let path = input
        .get("file_path")
        .or_else(|| input.get("path"))
        .or_else(|| input.get("TargetFile"))
        .or_else(|| input.get("AbsolutePath"))
        .and_then(Value::as_str)
        .map(str::to_string)
        .or_else(|| input.as_str().and_then(patch_file_paths))
        .or_else(|| input.get("patch").and_then(Value::as_str).and_then(patch_file_paths))
        .or_else(|| {
            input
                .get("command")
                .or_else(|| input.get("cmd"))
                .and_then(Value::as_str)
                .and_then(simple_shell_file_path)
        }).map(|path| crate::agent_conversation::normalization::tools::paths_in_workdir(&path, input));
    let diff = input
        .get("diff")
        .or_else(|| input.get("patch"))
        .and_then(Value::as_str)
        .map(str::to_string);

    let mut payload = std::collections::BTreeMap::from([
        ("kind".to_string(), Value::String("tool".to_string())),
        ("historical".to_string(), Value::Bool(true)),
        ("itemId".to_string(), Value::String(item_id.to_string())),
        ("title".to_string(), Value::String(name.to_string())),
        ("name".to_string(), Value::String(name.to_string())),
        ("status".to_string(), Value::String("completed".to_string())),
    ]);
    if !input.is_null() {
        payload.insert("rawInput".to_string(), input.clone());
    }
    if let Some(summary_text) = &summary {
        payload.insert("summary".to_string(), Value::String(summary_text.clone()));
    }
    if let Some(path_text) = &path {
        payload.insert("path".to_string(), Value::String(path_text.clone()));
    }
    if let Some(diff_text) = &diff {
        payload.insert("diff".to_string(), Value::String(diff_text.clone()));
    }

    ProjectedRecord {
        key: format!("tool:{item_id}"),
        event_type: AgentEventType::ItemCompleted,
        timestamp_ms,
        item_id: Some(item_id.to_string()),
        payload,
        native: Some(AgentConversationPayload::Tool {
            item_id: item_id.to_string(),
            name: name.to_string(),
            state: ToolState::Completed,
            summary,
            output: None,
            path,
            diff, has_detail: false, added: None, removed: None,
        }),
    }
}

/// The writing inside a tool result, however the provider wrote it.
///
/// Claude puts a string or a list of blocks under `content`; Codex puts a
/// string or a list of `input_text` under `output`. Both are the answer a tool
/// gave, which is the thing a reader opens a row to see.
pub fn tool_output_text(value: &Value) -> Option<String> {
    let text = match value {
        Value::String(text) => text.clone(),
        Value::Array(items) => items
            .iter()
            .filter_map(tool_output_text)
            .collect::<Vec<_>>()
            .join("\n"),
        Value::Object(fields) => {
            for key in ["text", "content", "output"] {
                if let Some(text) = fields.get(key).and_then(tool_output_text) {
                    return Some(text);
                }
            }
            String::new()
        }
        _ => String::new(),
    };
    let text = text.trim_end();
    (!text.is_empty()).then(|| text.to_string())
}

/// What a tool answered, said under the id of the call it answers.
///
/// A transcript writes the result on its own line, after the call and with no
/// tool name on it. Both lines carry the same id, so this is the same row
/// arriving twice: once for what was asked, once for what came back. The name
/// is left empty because this line does not know it, and the row already does.
pub fn tool_output_record(item_id: &str, output: String, timestamp_ms: u128) -> ProjectedRecord {
    ProjectedRecord {
        key: format!("tool-output:{item_id}"),
        event_type: AgentEventType::ItemCompleted,
        timestamp_ms,
        item_id: Some(item_id.to_string()),
        payload: std::collections::BTreeMap::from([
            ("kind".to_string(), Value::String("tool".to_string())),
            ("historical".to_string(), Value::Bool(true)),
            ("itemId".to_string(), Value::String(item_id.to_string())),
            ("state".to_string(), Value::String("completed".to_string())),
            ("output".to_string(), Value::String(output.clone())),
        ]),
        native: Some(AgentConversationPayload::Tool {
            item_id: item_id.to_string(),
            name: String::new(),
            state: ToolState::Completed,
            summary: None,
            output: Some(output),
            path: None,
            diff: None, has_detail: false, added: None, removed: None,
        }),
    }
}

pub fn stable_key(prefix: &str, line: &[u8]) -> String {
    let mut hasher = DefaultHasher::new();
    line.hash(&mut hasher);
    format!("{prefix}:{:016x}", hasher.finish())
}

/// When a transcript line says it happened, in milliseconds since the epoch.
///
/// Both providers write the time as an RFC 3339 string — `2026-08-01T12:34:56.789Z`
/// — and only a bare number was read here, so every line came back as zero. The
/// import then stamped each record with the moment the import itself ran, which
/// made a whole restored conversation share one timestamp. Nothing downstream
/// could tell how long a turn took, so a turn's fold had no duration to show.
pub fn timestamp(value: &Value) -> u128 {
    value
        .get("timestamp")
        .and_then(Value::as_u64)
        .map(u128::from)
        .or_else(|| {
            value
                .get("timestamp")
                .and_then(Value::as_str)
                .and_then(rfc3339_millis)
        })
        .or_else(|| {
            value
                .pointer("/payload/occurred_at_ms")
                .and_then(Value::as_u64)
                .map(u128::from)
        })
        .unwrap_or(0)
}

/// Reads an RFC 3339 instant, or a bare number already in milliseconds.
fn rfc3339_millis(text: &str) -> Option<u128> {
    if let Ok(millis) = text.parse::<u128>() {
        return Some(millis);
    }
    let parsed = chrono::DateTime::parse_from_rfc3339(text).ok()?;
    u128::try_from(parsed.timestamp_millis()).ok()
}

fn location(path: PathBuf) -> Result<TranscriptLocation, String> {
    let canonical_path = fs::canonicalize(&path)
        .map_err(|error| format!("Could not resolve transcript: {error}"))?;
    let metadata = canonical_path
        .metadata()
        .map_err(|error| error.to_string())?;
    Ok(TranscriptLocation {
        identity: file_identity(&metadata),
        len: metadata.len(),
        canonical_path,
    })
}

#[cfg(unix)]
fn file_identity(metadata: &Metadata) -> FileIdentity {
    use std::os::unix::fs::MetadataExt;
    FileIdentity {
        device: metadata.dev(),
        inode: metadata.ino(),
    }
}

#[cfg(not(unix))]
fn file_identity(metadata: &Metadata) -> FileIdentity {
    FileIdentity {
        device: 0,
        inode: metadata.len(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    mod codex_resume;
    #[test]
    fn snapshot_text_reports_when_it_discards_the_prefix() {
        let path = std::env::temp_dir().join(format!(
            "assembly-transcript-tail-{}-{}.jsonl",
            std::process::id(),
            SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .expect("clock after epoch")
                .as_nanos()
        ));
        fs::write(&path, []).expect("write empty transcript");
        let (empty, truncated) = read_snapshot_text(&path).expect("read empty transcript");
        assert!(empty.is_empty());
        assert!(!truncated);

        let mut input = vec![b'x'; RECONCILIATION_BYTES as usize + 1];
        input.extend_from_slice(b"\ntail\n");
        fs::write(&path, input).expect("write oversized transcript");
        let (tail, truncated) = read_snapshot_text(&path).expect("read transcript tail");
        assert_eq!(tail, "tail");
        assert!(truncated);
        fs::remove_file(path).expect("remove transcript fixture");
    }

    #[test]
    fn a_codex_raw_patch_keeps_every_edited_file() {
        let line = serde_json::json!({
            "timestamp": "2026-08-01T12:00:00.000Z",
            "type": "response_item",
            "payload": {
                "type": "custom_tool_call",
                "call_id": "patch_1",
                "name": "apply_patch",
                "input": "*** Begin Patch\n*** Update File: src/one.ts\n@@\n+change\n*** Add File: src/two.ts\n+new\n*** End Patch"
            }
        }).to_string();
        let records = parse_durable_line(AgentConversationProvider::Codex, "session-1", line.as_bytes());
        assert_eq!(records[0].payload.get("kind"), Some(&serde_json::json!("tool")));
        assert!(records[0].payload.contains_key("rawInput"));
        assert!(!records[0].payload.contains_key("args"));
        match records[0].native.as_ref().expect("tool event") {
            AgentConversationPayload::Tool { path, .. } => {
                assert_eq!(path.as_deref(), Some("src/one.ts\nsrc/two.ts"));
            }
            other => panic!("expected Tool, got {other:?}"),
        }
    }

    #[test]
    fn a_wrapped_compound_read_keeps_its_file_operands() {
        let command = "/bin/bash -lc \"sed -n '1,120p' read.txt && printf '--- edit.txt ---' && sed -n '1,120p' edit.txt\"";
        assert_eq!(simple_shell_file_path(command).as_deref(), Some("read.txt\nedit.txt"));
    }

    /// A tool row opens onto what the tool answered. Both providers write that
    /// answer on its own line, under the id of the call and with no tool name,
    /// and both readers skipped it — so a resumed conversation showed what each
    /// tool was asked and never what it said.
    #[test]
    fn a_claude_tool_result_is_read_as_the_answer_to_its_call() {
        let line = serde_json::json!({
            "timestamp": "2026-08-01T12:00:00.000Z",
            "type": "user",
            "sessionId": "session-1",
            "message": { "role": "user", "content": [{
                "type": "tool_result",
                "tool_use_id": "toolu_1",
                "content": "running 3 tests\nall passed"
            }]}
        })
        .to_string();
        let records = parse_durable_line(
            AgentConversationProvider::Claude,
            "session-1",
            line.as_bytes(),
        );
        let tool = records
            .iter()
            .find(|record| record.item_id.as_deref() == Some("toolu_1"))
            .expect("the answer is kept");
        assert_eq!(tool.payload.get("kind"), Some(&serde_json::json!("tool")));
        assert_eq!(tool.payload.get("itemId").and_then(Value::as_str), tool.item_id.as_deref());
        assert_eq!(tool.payload.get("state"), Some(&serde_json::json!("completed")));
        match tool.native.as_ref().expect("it is a tool event") {
            AgentConversationPayload::Tool { output, name, .. } => {
                assert_eq!(tool.payload.get("output").and_then(Value::as_str), output.as_deref());
                assert_eq!(output.as_deref(), Some("running 3 tests\nall passed"));
                assert!(
                    name.is_empty(),
                    "a result names no tool; the call already did"
                );
            }
            other => panic!("expected Tool, got {other:?}"),
        }
    }

    #[test]
    fn a_codex_tool_output_is_read_as_the_answer_to_its_call() {
        let line = serde_json::json!({
            "timestamp": "2026-08-01T12:00:00.000Z",
            "type": "response_item",
            "payload": {
                "type": "custom_tool_call_output",
                "call_id": "call_1",
                "output": [
                    { "type": "input_text", "text": "Script completed" },
                    { "type": "input_text", "text": "Wall time 0.1 seconds" }
                ]
            }
        })
        .to_string();
        let records = parse_durable_line(
            AgentConversationProvider::Codex,
            "session-1",
            line.as_bytes(),
        );
        let tool = records
            .iter()
            .find(|record| record.item_id.as_deref() == Some("call_1"))
            .expect("the answer is kept");
        assert_eq!(tool.payload.get("kind"), Some(&serde_json::json!("tool")));
        assert_eq!(tool.payload.get("itemId").and_then(Value::as_str), tool.item_id.as_deref());
        assert_eq!(tool.payload.get("state"), Some(&serde_json::json!("completed")));
        match tool.native.as_ref().expect("it is a tool event") {
            AgentConversationPayload::Tool { output, .. } => {
                assert_eq!(tool.payload.get("output").and_then(Value::as_str), output.as_deref());
                assert_eq!(
                    output.as_deref(),
                    Some("Script completed\nWall time 0.1 seconds")
                );
            }
            other => panic!("expected Tool, got {other:?}"),
        }
    }

    /// A resumed conversation shows where the agent threw the older part of it
    /// away. Without the record the transcript has a silent gap: the reply
    /// after a compaction reads as though it forgot what came before.
    #[test]
    fn a_claude_compaction_boundary_is_read_with_the_sizes_it_names() {
        let line = serde_json::json!({
            "timestamp": "2026-08-01T12:00:00.000Z",
            "type": "system",
            "subtype": "compact_boundary",
            "sessionId": "session-1",
            "content": "Conversation compacted",
            "compactMetadata": { "trigger": "auto", "preTokens": 351_238, "postTokens": 22_202 }
        })
        .to_string();
        let records = parse_durable_line(
            AgentConversationProvider::Claude,
            "session-1",
            line.as_bytes(),
        );
        assert_eq!(records.len(), 1, "one record, and it is the compaction");
        assert_eq!(records[0].payload.get("kind"), Some(&serde_json::json!("contextCompaction")));
        match records[0]
            .native
            .as_ref()
            .expect("it is a compaction event")
        {
            AgentConversationPayload::ContextCompaction {
                trigger,
                pre_tokens,
                post_tokens,
            } => {
                assert_eq!(trigger.as_deref(), Some("auto"));
                assert_eq!(*pre_tokens, Some(351_238));
                assert_eq!(*post_tokens, Some(22_202));
            }
            other => panic!("expected ContextCompaction, got {other:?}"),
        }
    }

    #[test]
    fn a_codex_compaction_is_read_even_though_it_names_no_sizes() {
        let line = serde_json::json!({
            "timestamp": "2026-08-01T12:00:00.000Z",
            "type": "event_msg",
            "payload": { "type": "context_compacted" }
        })
        .to_string();
        let records = parse_durable_line(
            AgentConversationProvider::Codex,
            "session-1",
            line.as_bytes(),
        );
        assert_eq!(records.len(), 1);
        assert_eq!(records[0].payload.get("kind"), Some(&serde_json::json!("contextCompaction")));
        assert_eq!(
            records[0].native,
            Some(AgentConversationPayload::ContextCompaction {
                trigger: None,
                pre_tokens: None,
                post_tokens: None,
            })
        );
    }

    #[test]
    fn reads_the_time_a_transcript_line_says_it_happened() {
        // Both providers write the time as an RFC 3339 string. Only a bare
        // number was read, so every imported record came back as zero and took
        // the import's own clock instead — one timestamp for a whole restored
        // conversation, and no turn with a duration to show.
        assert_eq!(
            timestamp(&object([(
                "timestamp",
                Value::String("2026-08-01T12:34:56.789Z".to_string())
            )])),
            1_785_587_696_789
        );
        assert_eq!(
            timestamp(&object([("timestamp", Value::from(1_785_587_696_789u64))])),
            1_785_587_696_789
        );
        assert_eq!(timestamp(&object([("timestamp", Value::Null)])), 0);
    }

    #[test]
    fn parses_claude_parent_and_metadata_without_sidechain_messages() {
        let input = r#"{"type":"user","uuid":"u1","sessionId":"s1","isSidechain":false,"permissionMode":"acceptEdits","message":{"content":"Hello"}}
{"type":"assistant","uuid":"a1","sessionId":"s1","isSidechain":false,"effort":"high","message":{"model":"claude-opus-5","usage":{"input_tokens":2,"cache_read_input_tokens":120},"content":[{"type":"text","text":"Answer"}]}}
{"type":"assistant","uuid":"child","sessionId":"s1","isSidechain":true,"agentId":"agent-a","message":{"content":[{"type":"text","text":"Hidden"}]}}"#;
        let records = input
            .lines()
            .flat_map(|line| {
                parse_durable_line(AgentConversationProvider::Claude, "s1", line.as_bytes())
            })
            .collect::<Vec<_>>();
        assert_eq!(
            records
                .iter()
                .filter(|record| record.event_type == AgentEventType::ItemCompleted)
                .count(),
            2
        );
        assert!(!records
            .iter()
            .any(|record| record.item_id.as_deref() == Some("child")));
        let config = records
            .iter()
            .find(|record| {
                record.event_type == AgentEventType::SessionConfigUpdated
                    && record.payload["config"]["model"] == "claude-opus-5"
            })
            .unwrap();
        assert_eq!(config.payload["config"]["model"], "claude-opus-5");
        assert_eq!(config.payload["config"]["effort"], "high");
        let usage = records
            .iter()
            .find(|record| record.event_type == AgentEventType::UsageUpdated)
            .unwrap();
        assert_eq!(usage.payload["usedTokens"], 122);
    }

    #[test]
    fn claude_child_projection_is_scoped_and_keeps_markdown_and_tools() {
        let line = r#"{"type":"assistant","uuid":"a1","sessionId":"parent","isSidechain":true,"agentId":"agent-a","message":{"content":[{"type":"text","text":"```rust\nfn main() {}\n```"},{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"/tmp/one.rs"}}]}}"#;
        let records = parse_durable_child_line(
            AgentConversationProvider::Claude, "parent", "agent-a", line.as_bytes(),
        );
        assert_eq!(records.len(), 2);
        assert!(matches!(records[0].native,
            Some(AgentConversationPayload::AssistantMessage { ref blocks, .. }) if blocks.is_none()));
        assert!(matches!(records[1].native,
            Some(AgentConversationPayload::Tool { ref name, .. }) if name == "Read"));
        assert!(parse_durable_child_line(
            AgentConversationProvider::Claude, "other", "agent-a", line.as_bytes(),
        ).is_empty());
        assert!(parse_durable_child_line(
            AgentConversationProvider::Claude, "parent", "agent-b", line.as_bytes(),
        ).is_empty());
        assert!(parse_durable_child_line(
            AgentConversationProvider::Claude, "parent", "agent-a",
            line.replace(r#","isSidechain":true"#, "").as_bytes(),
        ).is_empty());
        assert!(parse_durable_child_line(
            AgentConversationProvider::Claude, "parent", "agent-a",
            line.replace("\"isSidechain\":true", "\"isSidechain\":false").as_bytes(),
        ).is_empty());
    }

    #[test]
    fn parses_codex_metadata_messages_and_child_activity() {
        let input = r#"{"type":"turn_context","payload":{"model":"gpt-5.6-sol","effort":"medium","approval_policy":"never"}}
{"type":"response_item","payload":{"type":"message","id":"u1","role":"user","content":[{"type":"input_text","text":"Review this"}]}}
{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":1000},"last_token_usage":{"total_tokens":122},"model_context_window":258400}}}
{"type":"event_msg","payload":{"type":"sub_agent_activity","agent_thread_id":"child-1","agent_path":"/root/reviewer","kind":"started","occurred_at_ms":50}}"#;
        let records = input
            .lines()
            .flat_map(|line| {
                parse_durable_line(AgentConversationProvider::Codex, "s1", line.as_bytes())
            })
            .collect::<Vec<_>>();
        assert_eq!(
            records
                .iter()
                .filter(|record| record.event_type == AgentEventType::ItemCompleted)
                .count(),
            1
        );
        let config = records
            .iter()
            .find(|record| record.event_type == AgentEventType::SessionConfigUpdated)
            .unwrap();
        assert_eq!(config.payload["config"]["model"], "gpt-5.6-sol");
        let usage = records
            .iter()
            .find(|record| record.event_type == AgentEventType::UsageUpdated)
            .unwrap();
        assert_eq!(usage.payload["usedTokens"], 122);
        assert_eq!(usage.payload["totalTokens"], 1000);
        let children = records
            .iter()
            .find(|record| record.event_type == AgentEventType::ChildrenUpdated)
            .unwrap();
        assert_eq!(children.payload["children"][0]["state"], "historical");
    }

    #[test]
    fn projects_tool_calls_as_the_app_s_own_tool_events() {
        // A resumed conversation showed messages and nothing else: the tools the
        // agent used were never projected, so the import had nothing to store and
        // the transcript read as a wall of prose.
        let claude = r#"{"type":"assistant","uuid":"a1","sessionId":"s1","isSidechain":false,"message":{"content":[{"type":"text","text":"Reading it now"},{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"/tmp/one.rs","limit":120}}]}}"#;
        let codex = r#"{"type":"response_item","payload":{"type":"function_call","id":"fc_1","name":"exec_command","arguments":"{\"cmd\":\"git status --short\"}","call_id":"call_1"}}"#;

        let records =
            parse_durable_line(AgentConversationProvider::Claude, "s1", claude.as_bytes())
                .into_iter()
                .chain(parse_durable_line(
                    AgentConversationProvider::Codex,
                    "s1",
                    codex.as_bytes(),
                ))
                .filter_map(|record| match record.native {
                    Some(AgentConversationPayload::Tool {
                        item_id,
                        name,
                        summary,
                        ..
                    }) => Some((item_id, name, summary)),
                    _ => None,
                })
                .collect::<Vec<_>>();

        assert_eq!(
            records,
            vec![
                (
                    "toolu_1".to_string(),
                    "Read".to_string(),
                    Some("/tmp/one.rs".to_string())
                ),
                (
                    "call_1".to_string(),
                    "exec_command".to_string(),
                    Some("git status --short".to_string())
                )
            ]
        );
    }

    #[test]
    fn rejects_unsafe_child_session_ids() {
        assert!(safe_session_id("../secret").is_err());
        assert!(safe_session_id("child-ok_1").is_ok());
    }
}
