use std::collections::HashMap;

use serde_json::Value;
use similar::TextDiff;

use super::super::history::transcript;
use super::super::phone::MAX_TOOL_OUTPUT_BYTES;
use super::super::protocol::AgentConversationPayload;
use super::text_from_value;

pub(super) fn tool_item_id(update: &Value, turn_id: &str) -> String {
    update
        .get("toolCallId")
        .or_else(|| update.get("tool_call_id"))
        .and_then(Value::as_str)
        .map(str::to_string)
        .unwrap_or_else(|| format!("tool-{turn_id}"))
}

/// codex-acp 2.x streams a command's output as `_meta.terminal_output_delta`
/// chunks (2.2.2 frame logs, TSK-1589), and a stored tool event's output
/// replaces the one before it, so each chunk's event carries everything so
/// far. `live_tool_calls` holds the joined text and drops it when the call
/// finishes; the finishing frame has no chunk and the store carries the joined
/// output onto it.
pub(in crate::agent_conversation) fn join_terminal_output(
    live_tool_calls: &mut HashMap<String, String>,
    prior: Option<(String, String)>,
    params: &Value,
    payload: &mut AgentConversationPayload,
) {
    if let Some((id, text)) = prior {
        live_tool_calls.entry(id).or_insert(text);
    }
    let AgentConversationPayload::Tool { item_id, output, .. } = payload else {
        return;
    };
    let update = params.get("update").unwrap_or(params);
    let Some(chunk) = update.pointer("/_meta/terminal_output_delta/data").and_then(Value::as_str) else {
        return;
    };
    let joined = live_tool_calls.entry(item_id.clone()).or_default();
    joined.push_str(chunk);
    // Keep the newest output, at most what a tool call ever sends.
    if let Some(excess) = joined.len().checked_sub(MAX_TOOL_OUTPUT_BYTES) {
        joined.drain(..joined.ceil_char_boundary(excess));
    }
    *output = Some(joined.clone());
}

/// The output joined so far for the call this update carries a chunk for. Take it before
/// `update_raw_liveness` runs: a finishing update drops the call, and its own last chunk
/// must still join onto what came before. Pass it to `join_terminal_output`.
pub(in crate::agent_conversation) fn joined_output_so_far(
    live_tool_calls: &HashMap<String, String>,
    params: &Value,
) -> Option<(String, String)> {
    let update = params.get("update").unwrap_or(params);
    update.pointer("/_meta/terminal_output_delta/data")?;
    let id = update.get("toolCallId").and_then(Value::as_str)?;
    Some((id.to_string(), live_tool_calls.get(id)?.clone()))
}

/// The parts of an ACP tool call a transcript row can draw.
#[derive(Debug, Default)]
pub(in crate::agent_conversation) struct ToolDetails {
    pub(in crate::agent_conversation) name: String,
    /// Input detail; commands stay whole for the shell box and copy action.
    pub(in crate::agent_conversation) summary: Option<String>,
    /// What the call produced, kept whole.
    pub(in crate::agent_conversation) output: Option<String>,
    /// The file the call was about.
    pub(in crate::agent_conversation) path: Option<String>,
    /// A unified diff, when the call changed a file.
    pub(in crate::agent_conversation) diff: Option<String>,
}

/// Reads an ACP tool call into the parts a row draws.
///
/// This used to join `content` and `locations` into one string and keep only
/// that. The row then had a summary line and nothing else, so its expander
/// opened onto nothing: no command output, no file, no change. The structure is
/// in the update; it was being thrown away on the way to the database.
pub(in crate::agent_conversation) fn tool_details(update: &Value) -> ToolDetails {
    let mut output = Vec::new();
    let mut diff = Vec::new();
    let mut paths = Vec::new();

    match update.get("content") {
        Some(Value::Array(blocks)) => {
            for block in blocks {
                if block.get("type").and_then(Value::as_str) == Some("diff") {
                    let block_path = block.get("path").and_then(Value::as_str);
                    if let Some(block_path) = block_path {
                        if !paths.contains(&block_path.to_owned()) {
                            paths.push(block_path.to_owned());
                        }
                    }
                    let patch = unified_diff(
                        block
                            .get("oldText")
                            .and_then(Value::as_str)
                            .unwrap_or_default(),
                        block
                            .get("newText")
                            .and_then(Value::as_str)
                            .unwrap_or_default(),
                    );
                    if !patch.is_empty() {
                        diff.push(patch);
                    }
                } else if let Some(text) = text_from_value(block) {
                    match patch_in_text(&text) {
                        Some(patch) => {
                            if let Some(target) = patch_target_path(&text) {
                                if !paths.contains(&target) {
                                    paths.push(target);
                                }
                            }
                            diff.push(patch);
                        }
                        None => output.push(text),
                    }
                }
            }
        }
        Some(content) => {
            if let Some(text) = text_from_value(content) {
                match patch_in_text(&text) {
                    Some(patch) => {
                        if let Some(target) = patch_target_path(&text) {
                            if !paths.contains(&target) {
                                paths.push(target);
                            }
                        }
                        diff.push(patch);
                    }
                    None => output.push(text),
                }
            }
        }
        None => {}
    }

    // codex-acp 1.x sends a command's output whole; 2.x only in terminal chunks.
    if output.is_empty() {
        if let Some(text) = ["/rawOutput/formatted_output", "/_meta/terminal_output_delta/data"]
            .into_iter()
            .find_map(|pointer| update.pointer(pointer).and_then(Value::as_str).filter(|text| !text.is_empty()))
        {
            output.push(text.to_string());
        }
    }

    if let Some(location) = update.get("locations").and_then(text_from_value) {
        if !paths.contains(&location) {
            paths.push(location);
        }
    } else {
        for key in ["file_path", "path"] {
            if let Some(target) = update.get("rawInput").and_then(|input| input.get(key))
                .and_then(Value::as_str).filter(|target| !target.is_empty())
            {
                if !paths.contains(&target.to_owned()) {
                    paths.push(target.to_owned());
                }
            }
        }
    }
    if let Some(file) = update
        .pointer("/rawInput/command")
        .or_else(|| update.pointer("/rawInput/cmd"))
        .or_else(|| update.get("title"))
        .and_then(Value::as_str)
        .and_then(transcript::simple_shell_file_path)
    {
        if !paths.contains(&file) {
            paths.push(file);
        }
    }

    // Completion titles may contain the result, not the tool or its input.
    let title = if update.get("sessionUpdate").and_then(Value::as_str) == Some("tool_call_update") {
        ""
    } else {
        update.get("title").and_then(Value::as_str).unwrap_or("")
    };
    let name = update.pointer("/_meta/claudeCode/toolName")
        .and_then(Value::as_str)
        .or_else(|| match update.get("kind").and_then(Value::as_str) {
            Some("execute" | "command") => Some("command"),
            Some("read") => Some("Read"),
            Some("edit" | "file_change") => Some("Edit"),
            Some("search") => Some("Search"),
            _ => None,
        })
        .unwrap_or_else(|| title.split("```").next().unwrap_or("").trim())
        .to_string();
    let input = update.get("rawInput");
    let summary = ["command", "cmd", "description", "query", "pattern", "file_path", "path", "id"]
        .iter()
        .find_map(|key| input.and_then(|input| input.get(key)).and_then(Value::as_str))
        .or_else(|| (matches!(update.get("kind").and_then(Value::as_str), Some("execute" | "command"))
            && !title.is_empty() && title != name && title != "Tool").then_some(title))
        .map(|text| unwrap_console_block(text).to_string());
    let output = (!output.is_empty()).then(|| output.iter()
        .map(|text| unwrap_console_block(text)).collect::<Vec<_>>().join("\n"));

    ToolDetails {
        name,
        summary,
        output,
        path: (!paths.is_empty()).then(|| {
            paths_in_workdir(&paths.join("\n"), update.get("rawInput").unwrap_or(&Value::Null))
        }),
        diff: (!diff.is_empty()).then(|| diff.join("\n")),
    }
}

/// Remove only the provider's complete console wrapper, preserving its body.
pub(in crate::agent_conversation) fn unwrap_console_block(text: &str) -> &str {
    text.strip_prefix("```console\n")
        .and_then(|body| body.strip_suffix("\n```"))
        .unwrap_or(text)
}

/// The patch inside a tool's text output, when the text is one.
///
/// Not every provider sends a file change the way the protocol describes it.
/// Codex's apply-file-changes call hands over a whole `git diff` as ordinary
/// text, which used to be drawn as tool output: a wall of raw `+` and `-`
/// lines with no gutters, no colour and no fold, in the middle of a
/// conversation. It is a diff, so it should be read as one.
///
/// Only the hunks are kept. The `diff --git`, `index` and `---`/`+++` lines
/// name the file, which the row already shows in its own header, and passing
/// them on would draw them as context lines inside the change.
fn patch_in_text(text: &str) -> Option<String> {
    let mut lines = text.lines();
    let first = lines.find(|line| !line.trim().is_empty())?;
    let named = first.starts_with("diff --git ");
    // A bare patch with no `diff --git` header still has to be a whole one:
    // both file lines and at least one hunk, or any prose quoting a `---` rule
    // would be swallowed.
    let bare = first.starts_with("--- ")
        && text.lines().any(|line| line.starts_with("+++ "))
        && text.lines().any(|line| line.starts_with("@@ "));
    if !named && !bare {
        return None;
    }
    let hunks: Vec<&str> = text
        .lines()
        .skip_while(|line| !line.starts_with("@@ "))
        .collect();
    (!hunks.is_empty()).then(|| hunks.join("\n"))
}

/// The file a text patch changes, read from its header.
///
/// The `+++` side names it, except for a deletion, where that side is
/// `/dev/null` and the `---` side is the file that went away. Both carry
/// git's `a/` and `b/` prefixes.
fn patch_target_path(text: &str) -> Option<String> {
    let named = |line: &str, marker: &str| -> Option<String> {
        let rest = line.strip_prefix(marker)?.trim();
        if rest == "/dev/null" {
            return None;
        }
        let rest = rest.split('\t').next().unwrap_or(rest);
        Some(
            rest.strip_prefix("a/")
                .or_else(|| rest.strip_prefix("b/"))
                .unwrap_or(rest)
                .to_owned(),
        )
    };
    text.lines()
        .find_map(|line| named(line, "+++ "))
        .or_else(|| text.lines().find_map(|line| named(line, "--- ")))
}

/// A unified diff of one whole-file replacement.
///
/// ACP hands over the file before and after rather than a patch, so the change
/// has to be found. Each hunk keeps three lines of surrounding context so
/// distant edits do not retain all the unchanged text between them.
pub(in crate::agent_conversation) fn unified_diff(old_text: &str, new_text: &str) -> String {
    TextDiff::from_lines(old_text, new_text)
        .unified_diff()
        .context_radius(3)
        .to_string()
}

/// Each line of `paths`, if relative, joined onto the folder the call ran in
/// (`workdir` or `cwd` in its input), so a file outside the session's project
/// still resolves. Unchanged when the call recorded no absolute folder.
pub(in crate::agent_conversation) fn paths_in_workdir(paths: &str, input: &Value) -> String {
    let workdir = input.get("workdir").or_else(|| input.get("cwd")).and_then(Value::as_str)
        .map(|folder| folder.trim_end_matches('/')).filter(|folder| folder.starts_with('/'));
    let Some(workdir) = workdir else { return paths.to_string() };
    paths.lines().map(|path| if path.starts_with('/') || path.starts_with('~') {
        path.to_string()
    } else {
        format!("{workdir}/{}", path.trim_start_matches("./"))
    }).collect::<Vec<_>>().join("\n")
}

#[cfg(test)]
mod tests {
    use super::tool_details;
    use crate::agent_conversation::history::transcript::tool_record;
    use serde_json::json;

    #[test]
    fn a_relative_read_carries_the_full_path_of_the_folder_the_call_ran_in() {
        let update = json!({
            "sessionUpdate": "tool_call", "toolCallId": "read-1", "kind": "read",
            "title": "Read remote.rs",
            "rawInput": { "command": "sed -n 1,80p src/remote.rs", "workdir": "/home/me/other-repo" }
        });
        assert_eq!(tool_details(&update).path.as_deref(), Some("/home/me/other-repo/src/remote.rs"));

        let located = json!({
            "sessionUpdate": "tool_call", "toolCallId": "read-2", "kind": "read",
            "locations": [{ "path": "src/remote.rs" }, { "path": "/etc/hosts" }],
            "rawInput": { "cwd": "/home/me/other-repo/" }
        });
        assert_eq!(
            tool_details(&located).path.as_deref(),
            Some("/home/me/other-repo/src/remote.rs\n/etc/hosts")
        );

        // No recorded folder: the path stays as the call gave it.
        let bare = json!({ "sessionUpdate": "tool_call", "kind": "read", "rawInput": { "file_path": "src/main.rs" } });
        assert_eq!(tool_details(&bare).path.as_deref(), Some("src/main.rs"));
    }

    #[test]
    fn a_past_shell_read_carries_the_full_path_of_its_working_folder() {
        let input = json!({ "cmd": "sed -n '1,80p' src/remote.rs", "workdir": "/home/me/other-repo" });
        let record = tool_record("call-1", "exec_command", &input, 0);
        assert_eq!(record.payload.get("path"), Some(&json!("/home/me/other-repo/src/remote.rs")));
    }
}
