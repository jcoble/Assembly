//! AG-UI 1.0 on the wire: the chunks Rust sends a chat view, the
//! `metadata.tanstack` fields TanStack AI's client reads, the interrupt binding
//! that lets the stock client resolve a request, the run input it sends back,
//! and the commands for the AG-UI channel.
use super::agui_projection::{page_chunks, Answer};
use super::manager::AgentRuntimeManager;
use super::protocol::{
    AgentConversationEvent, AgentConversationItemPage, AgentConversationPayload, AgentConversationSelectionSnapshot,
    AgentConversationSendReceipt, ApprovalDecision, CommandResult, RespondAgentConversationApprovalRequest,
    RespondAgentConversationInputRequest, RespondAgentConversationPermissionRequest, SendAgentConversationMessageRequest,
};
use super::remote::RemoteConnectionManager;
use crate::projection_streams::{ProjectionStreams, StreamEnvelope};
use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};
use sha2::{Digest, Sha256};
use std::collections::HashSet;
use tauri::{ipc::Channel, State};

/// The binding version TanStack AI reads (`INTERRUPT_BINDING_VERSION`).
const BINDING_VERSION: u64 = 1;
/// Where TanStack AI looks for an interrupt's binding.
const BINDING_KEY: &str = "tanstack:interruptBinding";

/// The part of an AG-UI `RunAgentInput` Assembly acts on, plus the message the
/// run sends: the composer's prompt as `send_agent_conversation_message` takes
/// it, and the id the chat gave its own copy. A run that only answers carries none.
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RunAgentInput {
    pub thread_id: String,
    pub run_id: String,
    #[serde(default)]
    pub parent_run_id: Option<String>,
    #[serde(default)]
    pub resume: Vec<ResumeEntry>,
    #[serde(default)]
    pub message: Option<SendAgentConversationMessageRequest>,
    #[serde(default)]
    pub message_id: Option<String>,
}

/// One answer to an interrupt, as the stock client sends it on `resume`.
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResumeEntry {
    pub interrupt_id: String,
    pub status: ResumeStatus,
    #[serde(default)]
    pub payload: Value,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq)]
#[serde(rename_all = "lowercase")]
pub enum ResumeStatus {
    Resolved,
    Cancelled,
}

/// `parentRunId` is set only on a continuation; AG-UI refuses a null there.
pub fn run_started(thread_id: &str, run_id: &str, parent_run_id: Option<&str>) -> Value {
    let mut chunk = json!({ "type": "RUN_STARTED", "threadId": thread_id, "runId": run_id });
    if let Some(parent) = parent_run_id {
        chunk["parentRunId"] = json!(parent);
    }
    chunk
}

/// A run that ended without failing. `outcome` is the interrupt or cancelled
/// outcome; none means it completed. `usage` is AG-UI's `usage[]`.
pub fn run_finished(thread_id: &str, run_id: &str, outcome: Option<Value>, usage: Option<Value>) -> Value {
    let mut chunk = json!({ "type": "RUN_FINISHED", "threadId": thread_id, "runId": run_id });
    match outcome {
        Some(outcome) => chunk["outcome"] = outcome,
        None => chunk["metadata"] = tanstack(json!({ "finishReason": "stop" })),
    }
    if let Some(usage) = usage {
        chunk["usage"] = usage;
    }
    chunk
}

/// Sent only when the turn itself failed: the stock client ends the run on it.
pub fn run_error(thread_id: &str, run_id: &str, message: &str, code: Option<&str>) -> Value {
    let mut chunk = json!({ "type": "RUN_ERROR", "threadId": thread_id, "runId": run_id, "message": message });
    if let Some(code) = code {
        chunk["code"] = json!(code);
    }
    chunk
}

pub fn cancelled() -> Value {
    json!({ "type": "cancelled" })
}

pub fn interrupted(interrupts: Vec<Value>) -> Value {
    json!({ "type": "interrupt", "interrupts": interrupts })
}

pub fn text_start(message_id: &str, role: &str, metadata: Value) -> Value {
    json!({ "type": "TEXT_MESSAGE_START", "messageId": message_id, "role": role, "metadata": metadata })
}

pub fn text_content(message_id: &str, delta: &str) -> Value {
    json!({ "type": "TEXT_MESSAGE_CONTENT", "messageId": message_id, "delta": delta })
}

/// `metadata` is merged into the message's, such as `completed` and `blocks`.
pub fn text_end(message_id: &str, metadata: Value) -> Value {
    json!({ "type": "TEXT_MESSAGE_END", "messageId": message_id, "metadata": metadata })
}

pub fn tool_start(tool_call_id: &str, name: &str, parent_message_id: Option<&str>, metadata: Value) -> Value {
    let mut chunk = json!({ "type": "TOOL_CALL_START", "toolCallId": tool_call_id, "toolCallName": name, "metadata": metadata });
    if let Some(parent) = parent_message_id {
        chunk["parentMessageId"] = json!(parent);
    }
    chunk
}

pub fn tool_args(tool_call_id: &str, args: &Value) -> Value {
    json!({ "type": "TOOL_CALL_ARGS", "toolCallId": tool_call_id, "delta": args.to_string() })
}

/// `input` is the call's latest arguments; the stock client takes them as final.
pub fn tool_end(tool_call_id: &str, input: &Value) -> Value {
    json!({ "type": "TOOL_CALL_END", "toolCallId": tool_call_id, "input": input })
}

/// A failed call keeps its error on the call (`output-error`), never `RUN_ERROR`.
pub fn tool_result(tool_call_id: &str, content: &str, failed: bool) -> Value {
    let mut chunk = json!({ "type": "TOOL_CALL_RESULT", "toolCallId": tool_call_id,
        "messageId": format!("{tool_call_id}-result"), "role": "tool", "content": content });
    if failed {
        chunk["metadata"] = json!({ "tanstack": { "state": "output-error" } });
    }
    chunk
}

pub fn subagent_started(run_id: &str, name: &str, parent_tool_call_id: Option<&str>) -> Value {
    let mut chunk = json!({ "type": "SUBAGENT_STARTED", "subagentRunId": run_id, "name": name });
    if let Some(parent) = parent_tool_call_id {
        chunk["parentToolCallId"] = json!(parent);
    }
    chunk
}

pub fn subagent_finished(run_id: &str) -> Value {
    json!({ "type": "SUBAGENT_FINISHED", "subagentRunId": run_id })
}

pub fn subagent_error(run_id: &str, message: &str) -> Value {
    json!({ "type": "SUBAGENT_ERROR", "subagentRunId": run_id, "message": message })
}

/// A transcript row AG-UI has no event for (a plan, a compaction, an error):
/// an activity message of type `assembly.<kind>` carrying the native event.
pub fn activity(message_id: &str, event: &AgentConversationEvent) -> Value {
    let event = serde_json::to_value(event).unwrap_or_default();
    let kind = event["payload"]["kind"].as_str().unwrap_or("event").to_string();
    json!({ "type": "ACTIVITY_SNAPSHOT", "messageId": message_id, "activityType": format!("assembly.{kind}"),
        "content": { "event": event } })
}

/// Other events AG-UI has no event for travel as `CUSTOM assembly.<kind>`.
pub fn custom(name: &str, value: Value) -> Value {
    json!({ "type": "CUSTOM", "name": format!("assembly.{name}"), "value": value })
}

/// The `metadata.tanstack` namespace, where TanStack's client reads its own fields.
pub fn tanstack(fields: Value) -> Value {
    json!({ "tanstack": fields })
}

/// An interrupt descriptor carrying TanStack's binding object, so the stock
/// client shows it as resolvable for the run it interrupted. `metadata` holds
/// Assembly's own display fields beside the binding.
pub fn bound_interrupt(
    id: &str,
    reason: &str,
    message: &str,
    response_schema: &Value,
    interrupted_run_id: &str,
    generation: u64,
    mut metadata: Map<String, Value>,
) -> Value {
    metadata.insert(BINDING_KEY.into(), json!({
        "v": BINDING_VERSION,
        "kind": "generic",
        "interruptId": id,
        "interruptedRunId": interrupted_run_id,
        "generation": generation,
        "responseSchemaHash": response_schema_hash(response_schema),
    }));
    json!({ "id": id, "reason": reason, "message": message, "responseSchema": response_schema, "metadata": metadata })
}

/// TanStack's `defaultInterruptHash(canonicalInterruptJson(schema))`
/// (`ai/src/interrupt-serialization.ts`): SHA-256 over key-sorted JSON.
pub fn response_schema_hash(schema: &Value) -> String {
    format!("sha256:{:x}", Sha256::digest(canonical_json(schema).as_bytes()))
}

/// JSON text exactly as JavaScript's `JSON.stringify` writes it after TanStack
/// sorts each object's keys: keys in UTF-16 order, numbers in JavaScript form.
pub fn canonical_json(value: &Value) -> String {
    match value {
        Value::Null => "null".into(),
        Value::Bool(flag) => flag.to_string(),
        Value::Number(number) => js_number(number.as_f64().unwrap_or(0.0)),
        Value::String(text) => Value::String(text.clone()).to_string(),
        Value::Array(items) => format!("[{}]", items.iter().map(canonical_json).collect::<Vec<_>>().join(",")),
        Value::Object(map) => {
            let mut keys: Vec<&String> = map.keys().collect();
            keys.sort_by(|left, right| left.encode_utf16().cmp(right.encode_utf16()));
            let fields: Vec<String> = keys.iter()
                .map(|key| format!("{}:{}", Value::String((*key).clone()), canonical_json(&map[*key])))
                .collect();
            format!("{{{}}}", fields.join(","))
        }
    }
}

/// ECMAScript `Number::toString` for a finite number (ECMA-262 §6.1.6.1.20).
/// Rust's `{:e}` gives the same shortest round-trip digits; only the layout differs.
fn js_number(number: f64) -> String {
    if number == 0.0 {
        return "0".into();
    }
    if number < 0.0 {
        return format!("-{}", js_number(-number));
    }
    let scientific = format!("{number:e}");
    let (mantissa, exponent) = scientific.split_once('e').unwrap_or((&scientific, "0"));
    let digits = mantissa.replace('.', "");
    let count = digits.len() as i32;
    // The value is 0.<digits> × 10^point.
    let point = exponent.parse::<i32>().unwrap_or(0) + 1;
    if count <= point && point <= 21 {
        format!("{digits}{}", "0".repeat((point - count) as usize))
    } else if 0 < point && point <= 21 {
        format!("{}.{}", &digits[..point as usize], &digits[point as usize..])
    } else if -6 < point && point <= 0 {
        format!("0.{}{digits}", "0".repeat(-point as usize))
    } else {
        let mantissa = if count == 1 { digits.clone() } else { format!("{}.{}", &digits[..1], &digits[1..]) };
        let exponent = point - 1;
        format!("{mantissa}e{}{}", if exponent < 0 { '-' } else { '+' }, exponent.abs())
    }
}

/// A chat view's opening snapshot: the native one, with its page's events as
/// AG-UI chunks, which main's ordered snapshot folds in (run events left out).
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AguiSnapshot {
    #[serde(flatten)]
    pub snapshot: AgentConversationSelectionSnapshot,
    pub chunks: Vec<Value>,
    /// Run chunks to queue right after the snapshot: the client's runs that
    /// ended meanwhile, then one `RUN_FINISHED` carrying every request still waiting.
    pub runs: Vec<Value>,
}

/// A page of a chat for scrolling: the native page, its events as chunks.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AguiPage {
    #[serde(flatten)]
    pub page: AgentConversationItemPage,
    pub chunks: Vec<Value>,
}

impl From<AgentConversationItemPage> for AguiPage {
    fn from(mut page: AgentConversationItemPage) -> Self {
        let chunks = page_chunks(&page.events);
        page.events = Vec::new();
        Self { page, chunks }
    }
}

/// History keeps messages and leaves runs out, so nothing old reopens a pause;
/// a reply's streamed pieces are joined into one.
pub fn history(chunks: Vec<Value>) -> Vec<Value> {
    let mut kept: Vec<Value> = Vec::with_capacity(chunks.len());
    for chunk in chunks {
        if chunk["type"].as_str().is_some_and(|kind| kind.starts_with("RUN_")) {
            continue;
        }
        if let Some(last) = kept.last_mut() {
            if chunk["type"] == "TEXT_MESSAGE_CONTENT" && last["type"] == "TEXT_MESSAGE_CONTENT" && last["messageId"] == chunk["messageId"] {
                let joined = format!("{}{}", last["delta"].as_str().unwrap_or_default(), chunk["delta"].as_str().unwrap_or_default());
                last["delta"] = Value::String(joined);
                continue;
            }
        }
        kept.push(chunk);
    }
    kept
}

#[tauri::command]
/// The AG-UI channel: one envelope per native event, holding its chunks.
pub fn register_agui_stream(streams: State<'_, ProjectionStreams>, registration_id: String, channel: Channel<StreamEnvelope<Vec<Value>>>) {
    streams.register_agui(registration_id, channel);
}

#[tauri::command]
pub fn acknowledge_agui_stream(streams: State<'_, ProjectionStreams>, registration_id: String, frames: usize, bytes: u64) {
    streams.acknowledge_agui(&registration_id, frames, bytes);
}

#[tauri::command]
pub fn unregister_agui_stream(streams: State<'_, ProjectionStreams>, registration_id: String) {
    streams.unregister_agui(&registration_id);
}

#[tauri::command]
/// The chat view closed: its session is no longer projected. The session goes on.
pub fn close_agui_view(streams: State<'_, ProjectionStreams>, owned_id: String) {
    streams.with_agui_views(|views| views.close(&owned_id));
}

#[tauri::command]
/// AG-UI's run request. It records the client's run id for the turn that
/// follows, answers each `resume` entry through the same commands the approval
/// card uses, local or remote, then sends the run's message, if any.
pub async fn run_agui(
    streams: State<'_, ProjectionStreams>,
    manager: State<'_, AgentRuntimeManager>,
    remote: State<'_, RemoteConnectionManager>,
    input: RunAgentInput,
) -> CommandResult<Option<AgentConversationSendReceipt>> {
    let answers = streams.with_agui_views(|views| views.start_run(&input))?;
    let owned_id = input.thread_id;
    for answer in answers {
        match answer {
            Answer::Permission { request_id, generation, option_id } => {
                let request = RespondAgentConversationPermissionRequest { owned_id: owned_id.clone(), generation, request_id, option_id };
                super::respond_agent_conversation_permission(manager.clone(), remote.clone(), request).await?;
            }
            Answer::Approval { request_id, generation, accept } => {
                let decision = if accept { ApprovalDecision::Accept } else { ApprovalDecision::Decline };
                let request = RespondAgentConversationApprovalRequest { owned_id: owned_id.clone(), generation, request_id, decision };
                super::respond_agent_conversation_approval(manager.clone(), remote.clone(), request).await?;
            }
            Answer::Input { request_id, generation, action, content } => {
                let request = RespondAgentConversationInputRequest { owned_id: owned_id.clone(), generation, request_id, content, action };
                super::respond_agent_conversation_input(manager.clone(), remote.clone(), request).await?;
            }
        }
    }
    match input.message {
        Some(message) => super::send_agent_conversation_message(manager, remote, message).await.map(Some),
        None => Ok(None),
    }
}

#[tauri::command]
/// Opens a chat view on the AG-UI channel: the snapshot as chunks, then the
/// run chunks to queue after it. From here the channel carries this session.
pub async fn read_agui_snapshot(
    streams: State<'_, ProjectionStreams>,
    manager: State<'_, AgentRuntimeManager>,
    remote: State<'_, RemoteConnectionManager>,
    owned_id: String,
    request_id: u64,
    max_bytes: u32,
    minimum_generation: Option<u64>,
) -> CommandResult<Option<AguiSnapshot>> {
    streams.with_agui_views(|views| views.begin_open(&owned_id));
    let read = super::read_agent_conversation_selection(manager, remote, owned_id.clone(), request_id, max_bytes, minimum_generation).await;
    let snapshot = read.as_ref().ok().and_then(Option::as_ref);
    let mut events: Vec<AgentConversationEvent> = snapshot
        .map(|snapshot| snapshot.page.events.iter().chain(&snapshot.pending_events).cloned().collect())
        .unwrap_or_default();
    events.sort_by_key(|event| event.sequence);
    events.dedup_by_key(|event| event.sequence);
    let pending: HashSet<String> = snapshot
        .map(|snapshot| snapshot.pending_events.iter().filter_map(request_id_of).collect())
        .unwrap_or_default();
    let head = snapshot.map(|snapshot| snapshot.pending_sequence);
    let running = snapshot.is_some_and(|snapshot| snapshot.active_turn_id.is_some());
    let opened = streams.with_agui_views(|views| {
        let mut opened = views.finish_open(&owned_id, head.map(|head| (events.as_slice(), &pending, head, running)));
        for (generation, sequence, chunks) in std::mem::take(&mut opened.live) {
            streams.send_agui(owned_id.clone(), generation, sequence, chunks);
        }
        opened
    });
    let Some(mut snapshot) = read? else { return Ok(None) };
    snapshot.page.events = Vec::new();
    Ok(Some(AguiSnapshot { snapshot, chunks: opened.events, runs: opened.runs }))
}

#[tauri::command]
/// The page just older than `before_sequence`, as AG-UI chunks.
pub async fn list_agui_events_before(
    manager: State<'_, AgentRuntimeManager>,
    remote: State<'_, RemoteConnectionManager>,
    owned_id: String,
    before_sequence: i64,
    max_bytes: u32,
    request_id: u64,
) -> CommandResult<AguiPage> {
    let page = super::list_agent_conversation_items_before(manager, remote, owned_id, before_sequence, max_bytes, request_id).await?;
    Ok(page.into())
}

#[tauri::command]
/// The page just newer than `after_sequence`, as AG-UI chunks.
pub async fn list_agui_events_after(
    manager: State<'_, AgentRuntimeManager>,
    remote: State<'_, RemoteConnectionManager>,
    owned_id: String,
    after_sequence: i64,
    max_bytes: u32,
    request_id: u64,
) -> CommandResult<AguiPage> {
    let page = super::list_agent_conversation_items_after(manager, remote, owned_id, after_sequence, max_bytes, request_id).await?;
    Ok(page.into())
}

fn request_id_of(event: &AgentConversationEvent) -> Option<String> {
    match &event.payload {
        AgentConversationPayload::Approval { request_id, .. }
        | AgentConversationPayload::UserInputRequested { request_id, .. } => Some(request_id.clone()),
        _ => None,
    }
}

#[cfg(test)]
#[path = "agui_tests.rs"]
mod tests;
