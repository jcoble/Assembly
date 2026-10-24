pub(in crate::agent_conversation) mod tools;
pub(crate) mod turn_usage;

use serde_json::Value;

use super::protocol::{
    AgentCommandDescriptor, AgentConversationPayload, AgentEvent, PlanItem, ToolState,
};
use tools::{tool_details, tool_item_id};

pub(super) fn frontend_payload_from_canonical(
    event: &AgentEvent,
) -> Result<AgentConversationPayload, String> {
    let object = event
        .payload
        .iter()
        .map(|(key, value)| (key.clone(), value.clone()))
        .collect();
    serde_json::from_value(Value::Object(object))
        .map_err(|error| format!("Could not decode legacy conversation payload: {error}"))
}

pub(super) fn session_update_kind(params: &Value) -> Option<&str> {
    let update = params.get("update").unwrap_or(params);
    update
        .get("sessionUpdate")
        .or_else(|| update.get("session_update"))
        .or_else(|| update.get("type"))
        .and_then(Value::as_str)
}

pub(super) fn is_session_state_update(params: &Value) -> bool {
    matches!(
        session_update_kind(params),
        Some(
            "available_commands_update"
                | "available-commands-update"
                | "usage_update"
                | "usage-update"
                | "token_count"
                | "token-count"
        )
    )
}

fn parse_command_descriptors(value: Option<&Value>) -> Vec<AgentCommandDescriptor> {
    value
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|entry| {
            let id = entry
                .get("id")
                .or_else(|| entry.get("name"))
                .and_then(Value::as_str)
                .map(str::trim)
                .filter(|value| !value.is_empty())?
                .to_string();
            let label = entry
                .get("label")
                .or_else(|| entry.get("name"))
                .or_else(|| entry.get("id"))
                .and_then(Value::as_str)
                .map(str::trim)
                .filter(|value| !value.is_empty())
                .unwrap_or(&id)
                .to_string();
            let description = entry
                .get("description")
                .and_then(Value::as_str)
                .map(str::trim)
                .filter(|value| !value.is_empty())
                .map(str::to_string);
            let input = entry.get("input");
            let input_hint = entry
                .get("inputHint")
                .or_else(|| entry.get("input_hint"))
                .and_then(Value::as_str)
                .or_else(|| {
                    input
                        .and_then(|input| input.get("hint"))
                        .and_then(Value::as_str)
                })
                .map(str::trim)
                .filter(|value| !value.is_empty())
                .map(str::to_string);
            let provider_metadata = entry
                .get("providerMetadata")
                .or_else(|| entry.get("provider_metadata"))
                .and_then(|metadata| serde_json::from_value(metadata.clone()).ok());
            Some(AgentCommandDescriptor {
                id,
                label,
                description,
                input_hint,
                provider_metadata,
            })
        })
        .collect()
}

fn value_at<'a>(value: &'a Value, path: &[&str]) -> Option<&'a Value> {
    path.iter()
        .try_fold(value, |current, key| current.get(*key))
}

fn first_u64(value: &Value, paths: &[&[&str]]) -> Option<u64> {
    paths
        .iter()
        .find_map(|path| value_at(value, path).and_then(Value::as_u64))
}

fn parse_usage_payload(update: &Value) -> Option<AgentConversationPayload> {
    let usage = update.get("usage").unwrap_or(update);
    // What this number answers is "how full is the context window", so the last
    // request's total comes first. `total_token_usage` is every turn of the
    // session added together, cached reads included — it passes the window
    // inside an afternoon and reads as 100% full for the rest of the session.
    // It stays as a fallback for a report that carries nothing else.
    let used_tokens = first_u64(
        update,
        &[
            &["used"],
            &["usedTokens"],
            &["used_tokens"],
            &["totalTokens"],
            &["total_tokens"],
            &["info", "last_token_usage", "total_tokens"],
            &["info", "total_token_usage", "total_tokens"],
        ],
    )
    .or_else(|| {
        first_u64(
            usage,
            &[
                &["used"],
                &["usedTokens"],
                &["used_tokens"],
                &["totalTokens"],
                &["total_tokens"],
            ],
        )
    });
    let context_window = first_u64(
        update,
        &[
            &["size"],
            &["contextWindow"],
            &["context_window"],
            &["modelContextWindow"],
            &["model_context_window"],
            &["info", "model_context_window"],
        ],
    )
    .or_else(|| {
        first_u64(
            usage,
            &[
                &["size"],
                &["contextWindow"],
                &["context_window"],
                &["modelContextWindow"],
                &["model_context_window"],
            ],
        )
    });
    let input_tokens = first_u64(update, &[&["inputTokens"], &["input_tokens"]])
        .or_else(|| first_u64(usage, &[&["inputTokens"], &["input_tokens"]]));
    let output_tokens = first_u64(update, &[&["outputTokens"], &["output_tokens"]])
        .or_else(|| first_u64(usage, &[&["outputTokens"], &["output_tokens"]]));
    let total_tokens = first_u64(
        update,
        &[
            &["totalUsage"],
            &["total_usage"],
            &["info", "total_token_usage", "total_tokens"],
        ],
    )
    .or_else(|| first_u64(usage, &[&["totalUsage"], &["total_usage"]]));
    (used_tokens.is_some()
        || context_window.is_some()
        || input_tokens.is_some()
        || output_tokens.is_some()
        || total_tokens.is_some())
    .then_some(AgentConversationPayload::Usage {
        input_tokens,
        output_tokens,
        used_tokens,
        context_window,
        total_tokens,
    })
}

pub(super) fn payload_from_session_update_for_turn(
    params: &Value,
    active_turn_id: Option<&str>,
) -> Option<AgentConversationPayload> {
    let update = params.get("update").unwrap_or(params);
    let replay = is_replay_session_update(params);
    let Some(kind) = session_update_kind(params) else {
        crate::debug_log::stderr_log!("Ignoring ACP session update without a kind");
        return None;
    };
    let turn_id = active_turn_id.unwrap_or_else(|| {
        update
            .get("turnId")
            .or_else(|| update.get("turn_id"))
            .or_else(|| params.get("turnId"))
            .or_else(|| params.get("turn_id"))
            .and_then(Value::as_str)
            .unwrap_or("unknown")
    });

    if matches!(kind, "tool_call" | "tool_call_update")
        && update.get("kind").and_then(Value::as_str) == Some("subagent")
    {
        return child_update_payload(update, kind);
    }
    match kind {
        "available_commands_update" | "available-commands-update" => {
            Some(AgentConversationPayload::AvailableCommandsUpdate {
                available_commands: parse_command_descriptors(
                    update
                        .get("availableCommands")
                        .or_else(|| update.get("available_commands")),
                ),
            })
        }
        "usage_update" | "usage-update" | "token_count" | "token-count" => {
            parse_usage_payload(update)
        }
        "context_compaction" | "context-compaction" => {
            Some(AgentConversationPayload::ContextCompaction {
                trigger: update
                    .get("trigger")
                    .and_then(Value::as_str)
                    .map(str::to_string),
                pre_tokens: first_u64(update, &[&["preTokens"], &["pre_tokens"]]),
                post_tokens: first_u64(update, &[&["postTokens"], &["post_tokens"]]),
            })
        }
        "agent_message_chunk" if replay => {
            let text = update
                .get("content")
                .and_then(text_from_value)
                .unwrap_or_default();
            Some(AgentConversationPayload::AssistantMessage {
                item_id: update
                    .get("messageId")
                    .or_else(|| update.get("message_id"))
                    .and_then(Value::as_str)
                    .map(str::to_string)
                    .unwrap_or_else(|| format!("assistant-{turn_id}")),
                text,
                completed: true,
                blocks: None,
            })
        }
        "agent_message_chunk" => Some(AgentConversationPayload::AssistantDelta {
            item_id: update
                .get("messageId")
                .or_else(|| update.get("message_id"))
                .and_then(Value::as_str)
                .map(str::to_string)
                .unwrap_or_else(|| format!("assistant-{turn_id}")),
            delta: update
                .get("content")
                .and_then(text_from_value)
                .unwrap_or_default(),
        }),
        "user_message_chunk" => Some(AgentConversationPayload::UserMessage {
            item_id: update
                .get("messageId")
                .or_else(|| update.get("message_id"))
                .and_then(Value::as_str)
                .map(str::to_string)
                .unwrap_or_else(|| format!("user-{turn_id}")),
            text: update
                .get("content")
                .and_then(text_from_value)
                .unwrap_or_default(),
            completed: replay,
            attachment_ids: Vec::new(),
        }),
        "agent_thought_chunk" => {
            crate::debug_log::stderr_log!("Ignoring ACP agent thought update");
            None
        }
        "tool_call" => {
            let details = tool_details(update);
            Some(AgentConversationPayload::Tool {
                item_id: tool_item_id(update, turn_id),
                name: details.name,
                state: ToolState::Started,
                summary: details.summary,
                output: details.output,
                path: details.path,
                diff: details.diff, has_detail: false, added: None, removed: None,
            })
        }
        "tool_call_update" => {
            let state = match update.get("status").and_then(Value::as_str) {
                Some("completed") => ToolState::Completed,
                Some("failed") => ToolState::Failed,
                Some("in_progress") | None => ToolState::Updated,
                Some(status) => {
                    crate::debug_log::stderr_log!(
                        "Ignoring unknown ACP tool status while retaining update: {status}"
                    );
                    ToolState::Updated
                }
            };
            let details = tool_details(update);
            Some(AgentConversationPayload::Tool {
                item_id: tool_item_id(update, turn_id),
                name: details.name,
                state,
                summary: details.summary,
                output: details.output,
                path: details.path,
                diff: details.diff, has_detail: false, added: None, removed: None,
            })
        }
        "plan" => Some(AgentConversationPayload::Plan {
            items: update
                .get("entries")
                .and_then(Value::as_array)
                .into_iter()
                .flatten()
                .map(|entry| PlanItem {
                    text: entry
                        .get("content")
                        .and_then(text_from_value)
                        .unwrap_or_default(),
                    status: entry
                        .get("status")
                        .and_then(Value::as_str)
                        .unwrap_or("pending")
                        .to_string(),
                })
                .collect(),
        }),
        unknown => {
            crate::debug_log::stderr_log!("Ignoring unknown ACP session update kind: {unknown}");
            None
        }
    }
}

fn child_update_payload(update: &Value, update_kind: &str) -> Option<AgentConversationPayload> {
    let child_id = update
        .get("childSessionId")
        .or_else(|| update.get("child_session_id"))
        .and_then(Value::as_str)?
        .to_string();
    let parent_tool_call_id = update
        .get("parentToolCallId")
        .or_else(|| update.get("parent_tool_call_id"))
        .and_then(Value::as_str)?
        .to_string();
    let label = update
        .get("label")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|label| !label.is_empty())
        .map(str::to_string);
    let terminal = update_kind == "tool_call_update"
        && matches!(
            update.get("status").and_then(Value::as_str),
            Some("completed" | "failed" | "cancelled" | "canceled" | "stopped" | "done")
        );
    // Keep this line small because it is persisted and rendered in a compact row.
    let latest_activity = tool_details(update)
        .output
        .or_else(|| {
            update
                .get("title")
                .and_then(Value::as_str)
                .map(str::to_string)
        })
        .or_else(|| label.clone())
        .map(|text: String| text.chars().take(160).collect());
    Some(AgentConversationPayload::ChildUpdate {
        child_id,
        parent_tool_call_id,
        parent_id: None,
        transcript_id: None,
        label,
        state: if terminal { "finished" } else { "running" }.into(),
        latest_activity,
    })
}

pub(super) fn is_replay_session_update(params: &Value) -> bool {
    fn replay_flag(value: &Value) -> bool {
        value
            .get("_meta")
            .and_then(|metadata| metadata.get("replay"))
            .and_then(Value::as_bool)
            .unwrap_or(false)
    }

    replay_flag(params)
        || params
            .get("update")
            .or_else(|| params.get("sessionUpdate"))
            .is_some_and(replay_flag)
}

pub(super) fn text_from_value(value: &Value) -> Option<String> {
    let text = match value {
        Value::String(text) => text.clone(),
        Value::Array(values) => values
            .iter()
            .filter_map(text_from_value)
            .filter(|text| !text.is_empty())
            .collect::<Vec<_>>()
            .join("\n"),
        Value::Object(object) => {
            for field in ["text", "output", "path", "uri", "content"] {
                if let Some(text) = object.get(field).and_then(text_from_value) {
                    if !text.is_empty() {
                        return Some(text);
                    }
                }
            }
            String::new()
        }
        _ => String::new(),
    };
    (!text.is_empty()).then_some(text)
}
