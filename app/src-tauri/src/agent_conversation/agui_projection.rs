//! Native conversation events as AG-UI chunks, for the chat views that are open.
//!
//! One projector serves the one AG-UI channel. A session joins it when its view
//! reads a snapshot; from then on each of its events becomes chunks, with what
//! that view has already shown dropped: replays (by sequence), repeated message
//! starts, and the whole reply a provider re-sends when it finishes one
//! (`record_finished_reply` in `manager.rs`). Remote sessions reach the desktop
//! as the same native events, so they are projected here the same way.
use super::{agui::{self, ResumeStatus, RunAgentInput}, manager::tool_call};
use super::protocol::{
    AgentConversationEvent, AgentConversationPayload as Payload, AgentUserInputAction,
    AgentUserInputField, AgentUserInputKind, ApprovalState, ConversationConnectionState, ToolState, TurnState,
};
use serde_json::{json, Map, Value};
use std::collections::{BTreeMap, HashMap, HashSet};

/// What a legacy approval, which offers no options of its own, is answered with:
/// a decision, as the approval card answers it.
const DECISIONS: [(&str, &str, &str); 2] = [("accept", "Allow", "allow_once"), ("decline", "Deny", "reject_once")];

/// Claude says nothing when it throws the older part of a conversation away;
/// the used tokens dropping off a cliff is the only sign. Below the floor the
/// window is not full enough for a fall to mean one, and a modest fall is an
/// agent reporting its last request rather than the whole session.
const COMPACTION_FLOOR_TOKENS: u64 = 32_000;
const COMPACTION_DROP_RATIO: f64 = 0.6;

/// A request the agent is waiting on, kept so it can be shown again and answered.
#[derive(Clone, Debug)]
struct OpenRequest {
    id: String,
    generation: u64,
    is_input: bool,
    /// The option ids a permission offers; empty when it is answered with `DECISIONS`.
    option_ids: Vec<String>,
    reason: &'static str,
    message: String,
    schema: Value,
    metadata: Map<String, Value>,
}

/// An answer from `resume`, for the existing permission and input handlers.
#[derive(Clone, Debug, PartialEq)]
pub enum Answer {
    Permission { request_id: String, generation: u64, option_id: String },
    Approval { request_id: String, generation: u64, accept: bool },
    Input { request_id: String, generation: u64, action: AgentUserInputAction, content: BTreeMap<String, Value> },
}

/// What opening a view produced: its history as chunks, the run chunks to
/// queue right after it (the client's runs that ended meanwhile, then one
/// carrying every open request), and chunks for events that arrived while it
/// was read.
#[derive(Default)]
pub struct Opened {
    pub events: Vec<Value>,
    pub runs: Vec<Value>,
    pub live: Vec<(u64, i64, Vec<Value>)>,
}

/// A call still open: its latest arguments, output and diff, as updates may
/// carry each one only once.
#[derive(Default)]
struct OpenCall {
    args: Map<String, Value>,
    output: Option<String>,
    diff: Option<String>,
}

#[derive(Default)]
struct View {
    last_sequence: Option<i64>,
    /// The run the view is in, its `RUN_STARTED` sent.
    open_run: Option<String>,
    /// The id and parent the client gave for the next run (`run_agui`), or the
    /// parent an answered pause leaves for its continuation.
    next_run: Option<String>,
    next_parent: Option<String>,
    /// The id the chat gave the message the client's run sends, which the
    /// chat already shows; the recorded copy takes it instead of its own.
    next_user: Option<String>,
    /// The run that ended in an interrupt and is waiting on `requests`.
    paused_run: Option<String>,
    requests: Vec<OpenRequest>,
    /// Text sent for each message still open, and what ended this turn.
    texts: HashMap<String, String>,
    ended: HashSet<String>,
    tools: HashMap<String, OpenCall>,
    /// Child agents seen this turn, and whether each has finished.
    children: HashMap<String, bool>,
    usage: Option<Value>,
    /// The latest occupancy reported, to see a compaction no event announced.
    used_tokens: Option<u64>,
    error: Option<(String, String)>,
    /// Events held while this view's snapshot is read.
    held: Option<Vec<AgentConversationEvent>>,
}

#[derive(Default)]
pub struct AguiProjector {
    views: HashMap<String, View>,
}

impl AguiProjector {
    /// Chunks for one live event; nothing for a session with no open view.
    pub fn project(&mut self, event: &AgentConversationEvent) -> Vec<Value> {
        let Some(view) = self.views.get_mut(&event.owned_id) else { return Vec::new() };
        if let Some(held) = &mut view.held {
            held.push(event.clone());
            return Vec::new();
        }
        let chunks = view.project(event);
        if matches!(event.payload, Payload::Connection { state: ConversationConnectionState::Closed, .. }) {
            self.close(&event.owned_id);
        }
        chunks
    }

    /// Stops projecting a session: its view closed, or the session did.
    pub fn close(&mut self, owned_id: &str) {
        self.views.remove(owned_id);
    }

    /// Holds the session's live events until `finish_open` has its snapshot.
    pub fn begin_open(&mut self, owned_id: &str) {
        self.views.entry(owned_id.to_string()).or_default().held.get_or_insert_with(Vec::new);
    }

    /// Restarts the view from the snapshot's events (`head` is the newest
    /// sequence it covers; `pending` the requests still waiting; `running`
    /// whether a turn is still going), then projects
    /// what arrived meanwhile. With no snapshot the view keeps what it had.
    pub fn finish_open(&mut self, owned_id: &str, snapshot: Option<(&[AgentConversationEvent], &HashSet<String>, i64, bool)>) -> Opened {
        let Some(view) = self.views.get_mut(owned_id) else { return Opened::default() };
        let held = view.held.take().unwrap_or_default();
        let mut opened = Opened::default();
        if let Some((events, pending, head, running)) = snapshot {
            let mut fresh = View::default();
            let chunks: Vec<Value> = events.iter().flat_map(|event| fresh.project(event)).collect();
            (fresh.next_run, fresh.next_parent, fresh.next_user) = (view.next_run.take(), view.next_parent.take(), view.next_user.take());
            // The journal can show a request the session no longer waits on.
            fresh.requests.retain(|request| pending.contains(&request.id));
            if fresh.requests.is_empty() {
                fresh.paused_run = None;
            }
            // History carries no turn ends, so the session says whether a turn still runs: the
            // client's run goes on while it does and ends after; a run the replay opened was never
            // shown, so live content starts its own. The client's pause lasts while its requests do.
            match view.open_run.take() {
                Some(old) if !running => opened.runs.push(agui::run_finished(owned_id, &old, None, None)),
                old => fresh.open_run = old,
            }
            match view.paused_run.take() {
                Some(old) if fresh.paused_run.is_none() => opened.runs.push(agui::run_finished(owned_id, &old, None, None)),
                old => fresh.paused_run = old.or(fresh.paused_run.take()),
            }
            fresh.last_sequence = Some(fresh.last_sequence.map_or(head, |last| last.max(head)));
            opened.events = agui::history(chunks);
            opened.runs.extend(fresh.pause_chunk(owned_id));
            *view = fresh;
        }
        for event in held {
            let chunks = view.project(&event);
            if !chunks.is_empty() {
                opened.live.push((event.generation, event.sequence, chunks));
            }
        }
        opened
    }

    /// Records the client's run for the turn it starts or continues, and turns
    /// each `resume` entry into an answer for the request it names.
    pub fn start_run(&mut self, input: &RunAgentInput) -> Result<Vec<Answer>, String> {
        let view = self.views.entry(input.thread_id.clone()).or_default();
        let answers = input.resume.iter().map(|entry| {
            let request = view.requests.iter().find(|request| request.id == entry.interrupt_id)
                .ok_or_else(|| format!("Nothing is waiting on interrupt {}", entry.interrupt_id))?;
            answer(request, entry.status, &entry.payload)
        }).collect::<Result<Vec<_>, String>>()?;
        view.next_run = Some(input.run_id.clone());
        view.next_parent = input.parent_run_id.clone();
        view.next_user = input.message_id.clone();
        Ok(answers)
    }
}

/// A page of older history as chunks, outside any view.
pub fn page_chunks(events: &[AgentConversationEvent]) -> Vec<Value> {
    let mut view = View::default();
    agui::history(events.iter().flat_map(|event| view.project(event)).collect())
}

fn answer(request: &OpenRequest, status: ResumeStatus, payload: &Value) -> Result<Answer, String> {
    let resolved = status == ResumeStatus::Resolved;
    let (request_id, generation) = (request.id.clone(), request.generation);
    if request.is_input {
        let content = if resolved {
            serde_json::from_value(payload.clone()).map_err(|_| "An answer to a question must be an object".to_string())?
        } else {
            BTreeMap::new()
        };
        let action = if resolved { AgentUserInputAction::Accept } else { AgentUserInputAction::Cancel };
        return Ok(Answer::Input { request_id, generation, action, content });
    }
    if !resolved {
        return Ok(Answer::Approval { request_id, generation, accept: false });
    }
    let option_id = payload["optionId"].as_str().unwrap_or_default();
    if request.option_ids.iter().any(|offered| offered == option_id) {
        return Ok(Answer::Permission { request_id, generation, option_id: option_id.to_string() });
    }
    if request.option_ids.is_empty() && DECISIONS.iter().any(|(id, ..)| *id == option_id) {
        return Ok(Answer::Approval { request_id, generation, accept: option_id == "accept" });
    }
    Err(format!("{option_id:?} is not one of the options request {} offered", request.id))
}

/// A permission's answer is one of its options, by id.
fn permission_request(id: &str, generation: u64, summary: &str, options: &[(String, String, String)]) -> OpenRequest {
    let offered: Vec<(&str, &str, &str)> = if options.is_empty() {
        DECISIONS.to_vec()
    } else {
        options.iter().map(|(id, name, kind)| (id.as_str(), name.as_str(), kind.as_str())).collect()
    };
    let ids: Vec<&str> = offered.iter().map(|(id, ..)| *id).collect();
    let mut metadata = Map::new();
    metadata.insert("assembly:options".into(), offered.iter()
        .map(|(id, name, kind)| json!({ "optionId": id, "name": name, "kind": kind })).collect());
    OpenRequest {
        id: id.to_string(), generation, is_input: false, reason: "permission", message: summary.to_string(),
        option_ids: options.iter().map(|(id, ..)| id.clone()).collect(),
        schema: json!({ "type": "object", "properties": { "optionId": { "type": "string", "enum": ids } }, "required": ["optionId"] }),
        metadata,
    }
}

fn input_schema(fields: &[AgentUserInputField]) -> Value {
    let mut properties = Map::new();
    let mut required = Vec::new();
    for field in fields {
        let mut property = json!({ "title": field.label });
        if let Some(description) = &field.description {
            property["description"] = json!(description);
        }
        let values: Option<Vec<Value>> = field.choices.as_ref().map(|choices| choices.iter().map(|choice| choice.value.clone()).collect());
        match field.kind {
            AgentUserInputKind::Text | AgentUserInputKind::Password => property["type"] = json!("string"),
            AgentUserInputKind::Boolean => property["type"] = json!("boolean"),
            AgentUserInputKind::Select => if let Some(values) = values { property["enum"] = json!(values) },
            AgentUserInputKind::MultiSelect => {
                property["type"] = json!("array");
                if let Some(values) = values { property["items"] = json!({ "enum": values }) }
            }
        }
        if field.required {
            required.push(field.id.clone());
        }
        properties.insert(field.id.clone(), property);
    }
    json!({ "type": "object", "properties": properties, "required": required })
}

/// What the timeline draws a row from, on every message and call start.
fn facts(event: &AgentConversationEvent, item_type: &str) -> Value {
    json!({ "itemType": item_type, "turnId": event.turn_id, "startedAtMs": event.timestamp_ms as u64, "firstSequence": event.sequence })
}

fn reply_facts(event: &AgentConversationEvent) -> Value {
    let mut facts = facts(event, "assistant-message");
    facts["completed"] = json!(false);
    facts
}

impl View {
    fn project(&mut self, event: &AgentConversationEvent) -> Vec<Value> {
        if self.last_sequence.is_some_and(|last| event.sequence <= last) {
            return Vec::new();
        }
        self.last_sequence = Some(event.sequence);
        let (thread, sequence) = (event.owned_id.as_str(), event.sequence);
        let mut out = Vec::new();
        let mut custom = false;
        match &event.payload {
            Payload::UserMessage { item_id, text, completed, attachment_ids } => {
                // A message starts the turn it asks for, or steers the open one.
                self.open(&mut out, thread, sequence);
                let mut metadata = facts(event, "user-message");
                if !attachment_ids.is_empty() {
                    metadata["attachmentIds"] = json!(attachment_ids);
                }
                let id = self.next_user.take().unwrap_or_else(|| item_id.clone());
                self.text(&mut out, &id, "user", text, *completed, metadata, json!({ "completed": true }));
            }
            Payload::AssistantDelta { item_id, delta } => {
                self.open(&mut out, thread, sequence);
                if !self.ended.contains(item_id) {
                    let sent = self.texts.entry(item_id.clone()).or_insert_with(|| {
                        out.push(agui::text_start(item_id, "assistant", reply_facts(event)));
                        String::new()
                    });
                    if !delta.is_empty() {
                        sent.push_str(delta);
                        out.push(agui::text_content(item_id, delta));
                    }
                }
            }
            // The reply re-sent whole at a turn's end adds nothing once ended, or
            // while paused (Stop expires the request first); `finish` ends it.
            Payload::AssistantMessage { item_id, .. }
                if self.ended.contains(item_id)
                    || (self.open_run.is_none() && self.paused_run.is_some() && self.texts.contains_key(item_id)) => {}
            Payload::AssistantMessage { item_id, text, completed, blocks } => {
                self.open(&mut out, thread, sequence);
                let mut end = json!({ "completed": true });
                if let Some(blocks) = blocks {
                    end["blocks"] = json!(blocks);
                }
                self.text(&mut out, item_id, "assistant", text, *completed, reply_facts(event), end);
            }
            Payload::Tool { item_id, name, state, summary, path, output, diff, .. } => {
                self.open(&mut out, thread, sequence);
                self.tool(&mut out, event, item_id, name, *state, [summary, path, output, diff]);
            }
            Payload::ChildUpdate { child_id, parent_tool_call_id, label, state, .. } => {
                self.child(&mut out, child_id, parent_tool_call_id, label.as_deref(), state);
                custom = true;
            }
            Payload::Approval { request_id, state: ApprovalState::Requested, summary, options } => {
                self.request(&mut out, thread, sequence, permission_request(request_id, event.generation, summary, options));
            }
            // Withdrawn, not answered: the turn's end usually follows (Stop, a failure).
            Payload::Approval { request_id, state: ApprovalState::Expired, .. }
            | Payload::UserInputResolved { request_id, cancelled: true } => {
                let before = self.requests.len();
                self.requests.retain(|open| open.id != *request_id);
                if self.requests.len() < before {
                    // What still waits is shown again; with nothing left it is empty.
                    out.extend(self.pause_chunk(thread));
                }
            }
            Payload::Approval { request_id, .. } | Payload::UserInputResolved { request_id, .. } => {
                self.answered(&mut out, thread, request_id);
            }
            Payload::UserInputRequested { request_id, title, description, fields, can_decline } => {
                let mut metadata = Map::new();
                metadata.insert("assembly:input".into(), json!({ "description": description, "canDecline": can_decline, "fields": fields }));
                self.request(&mut out, thread, sequence, OpenRequest {
                    id: request_id.clone(), generation: event.generation, is_input: true, option_ids: Vec::new(), reason: "input",
                    message: title.clone(), schema: input_schema(fields), metadata,
                });
            }
            Payload::Turn { state: TurnState::Started, .. } => self.open(&mut out, thread, sequence),
            Payload::Turn { state, .. } => self.finish(&mut out, thread, *state, sequence),
            Payload::Connection { state: ConversationConnectionState::Disconnected | ConversationConnectionState::Failed
                | ConversationConnectionState::Closed, .. } => {
                // The agent is gone: no answer or reply will come for what is open.
                self.requests.clear();
                self.finish(&mut out, thread, TurnState::Interrupted, sequence);
                custom = true;
            }
            Payload::Usage { input_tokens, output_tokens, used_tokens, total_tokens, .. } => {
                if let Some(used) = *used_tokens {
                    if let Some(pre) = self.used_tokens.filter(|pre| *pre >= COMPACTION_FLOOR_TOKENS && used as f64 <= *pre as f64 * COMPACTION_DROP_RATIO) {
                        let mut marker = event.clone();
                        marker.payload = Payload::ContextCompaction { trigger: None, pre_tokens: Some(pre), post_tokens: Some(used) };
                        out.push(agui::activity(&format!("compaction:{sequence}"), &marker));
                    }
                    self.used_tokens = Some(used);
                }
                let mut usage = Map::new();
                for (key, value) in [("inputTokens", input_tokens), ("outputTokens", output_tokens), ("totalTokens", total_tokens)] {
                    if let Some(value) = value {
                        usage.insert(key.into(), json!(value));
                    }
                }
                if !usage.is_empty() {
                    usage.insert("provider".into(), json!(event.provider));
                    self.usage = Some(json!([usage]));
                }
                custom = true;
            }
            Payload::Error { code, message, .. } => {
                self.error = Some((code.clone(), message.clone()));
                out.push(agui::activity(&format!("error:{}:{sequence}", event.generation), event));
            }
            // Rows AG-UI has no event for; none of them opens a run.
            Payload::Plan { .. } => out.push(agui::activity(&format!("plan:{sequence}"), event)),
            Payload::ContextCompaction { .. } => out.push(agui::activity(&format!("compaction:{sequence}"), event)),
            Payload::CheckoutChanged { .. } => out.push(agui::activity(&format!("checkout:{sequence}"), event)),
            Payload::TerminalProjection(projection) => {
                let id = projection.item_id.clone().unwrap_or_else(|| format!("terminalProjection:{sequence}"));
                out.push(agui::activity(&id, event));
            }
            _ => custom = true,
        }
        if custom {
            if let Ok(value) = serde_json::to_value(&event.payload) {
                let kind = value["kind"].as_str().unwrap_or("event").to_string();
                out.push(agui::custom(&kind, value));
            }
        }
        out
    }

    /// Opens a run for content that arrives outside one. Content during a pause
    /// gets a run of its own; a pause whose requests were withdrawn ends first.
    fn open(&mut self, out: &mut Vec<Value>, thread: &str, sequence: i64) {
        if self.open_run.is_some() {
            return;
        }
        if let Some(paused) = self.paused_run.take_if(|_| self.requests.is_empty()) {
            out.push(agui::run_finished(thread, &paused, None, None));
            self.next_parent.get_or_insert(paused);
        }
        let run_id = self.next_run.take().unwrap_or_else(|| format!("run-{sequence}"));
        out.push(agui::run_started(thread, &run_id, self.next_parent.take().as_deref()));
        self.open_run = Some(run_id);
        // A failed prompt's error arrives after its turn ended; it is not this run's.
        self.error = None;
    }

    /// A whole-message event: start it once, send only text the view hasn't
    /// shown, and end it once.
    #[allow(clippy::too_many_arguments)]
    fn text(&mut self, out: &mut Vec<Value>, id: &str, role: &str, text: &str, completed: bool, start: Value, end: Value) {
        if self.ended.contains(id) {
            return;
        }
        match self.texts.get_mut(id) {
            None => {
                out.push(agui::text_start(id, role, start));
                if !text.is_empty() {
                    out.push(agui::text_content(id, text));
                }
                self.texts.insert(id.to_string(), text.to_string());
            }
            Some(sent) => {
                if let Some(rest) = text.strip_prefix(sent.as_str()) {
                    if !rest.is_empty() {
                        out.push(agui::text_content(id, rest));
                        *sent = text.to_string();
                    }
                }
            }
        }
        if completed {
            self.texts.remove(id);
            self.ended.insert(id.to_string());
            out.push(agui::text_end(id, end));
        }
    }

    /// Starts a call once, as a message of its own, with its arguments once known; ends it with the
    /// latest ones and the native call as its result, which the client draws the row from.
    fn tool(&mut self, out: &mut Vec<Value>, event: &AgentConversationEvent, id: &str, name: &str, state: ToolState, fields: [&Option<String>; 4]) {
        let [summary, path, output, diff] = fields;
        if self.ended.contains(id) { // Imported history sends a finished call's output in an event of its own.
            if output.is_some() || diff.is_some() || matches!(event.payload, Payload::Tool { has_detail: true, .. }) {
                out.push(agui::tool_result(id, &serde_json::to_string(&tool_call::without_details(event.clone()).payload).unwrap_or_default(), state == ToolState::Failed));
            }
            return;
        }
        let call = self.tools.entry(id.to_string()).or_insert_with(|| {
            out.push(agui::tool_start(id, name, Some(id), facts(event, "mcp-tool")));
            OpenCall::default()
        });
        let first = call.args.is_empty();
        for (key, value) in [("summary", summary), ("path", path)] { if let Some(value) = value { call.args.insert(key.into(), json!(value)); } }
        call.output = output.clone().or(call.output.take());
        call.diff = diff.clone().or(call.diff.take());
        if first && !call.args.is_empty() {
            out.push(agui::tool_args(id, &Value::Object(call.args.clone())));
        }
        if !matches!(state, ToolState::Completed | ToolState::Failed) {
            // A running call's output so far reaches an open row; each result replaces the one before.
            if output.as_deref().is_some_and(|text| !text.is_empty()) {
                out.push(agui::tool_result(id, &serde_json::to_string(&event.payload).unwrap_or_default(), false));
            }
            return;
        }
        let call = self.tools.remove(id).unwrap_or_default();
        out.push(agui::tool_end(id, &Value::Object(call.args)));
        // The finished row keeps what it shows closed; opening it reads the output and diff by id.
        let mut finished = event.clone();
        if let Payload::Tool { output, diff, .. } = &mut finished.payload { (*output, *diff) = (call.output, call.diff); }
        let payload = serde_json::to_string(&tool_call::without_details(finished).payload).unwrap_or_default();
        out.push(agui::tool_result(id, &payload, state == ToolState::Failed));
        self.ended.insert(id.to_string());
    }

    fn child(&mut self, out: &mut Vec<Value>, id: &str, parent_tool_call_id: &str, label: Option<&str>, state: &str) {
        let done = matches!(state, "finished" | "done" | "completed" | "cancelled" | "stopped");
        let failed = matches!(state, "failed" | "error" | "disconnected");
        if (done || failed) && !self.children.contains_key(id) {
            // Closed when an earlier turn ended.
            return;
        }
        let finished = self.children.entry(id.to_string()).or_insert_with(|| {
            let parent = (!parent_tool_call_id.is_empty()).then_some(parent_tool_call_id);
            out.push(agui::subagent_started(id, label.unwrap_or("Subagent"), parent));
            false
        });
        if *finished {
            return;
        }
        if done {
            out.push(agui::subagent_finished(id));
        } else if failed {
            out.push(agui::subagent_error(id, state));
        } else {
            return;
        }
        *finished = true;
    }

    /// A request ends the run it interrupts; a second one joins the same pause.
    fn request(&mut self, out: &mut Vec<Value>, thread: &str, sequence: i64, request: OpenRequest) {
        if self.requests.is_empty() {
            self.open(out, thread, sequence);
        }
        if self.paused_run.is_none() {
            self.paused_run = self.open_run.take();
        }
        self.requests.retain(|open| open.id != request.id);
        self.requests.push(request);
        out.extend(self.pause_chunk(thread));
    }

    /// An answer from any surface (this view's `resume`, the approval card, the
    /// inbox, the phone) finishes the paused run at once, so the stock client
    /// drops the interrupt and unlocks; the continuation names it as parent.
    /// A withdrawn request is not an answer: the turn's end or `open` ends it.
    fn answered(&mut self, out: &mut Vec<Value>, thread: &str, request_id: &str) {
        let before = self.requests.len();
        self.requests.retain(|open| open.id != request_id);
        if self.requests.len() == before {
            return;
        }
        let Some(paused) = self.paused_run.clone() else { return };
        if self.requests.is_empty() {
            out.push(agui::run_finished(thread, &paused, None, None));
            self.paused_run = None;
            self.next_parent = Some(paused);
        } else {
            out.extend(self.pause_chunk(thread));
        }
    }

    fn finish(&mut self, out: &mut Vec<Value>, thread: &str, state: TurnState, sequence: i64) {
        // Replies, calls and child agents still open end before their run does.
        let mut texts: Vec<String> = self.texts.drain().map(|(id, _)| id).collect();
        texts.sort();
        out.extend(texts.iter().map(|id| agui::text_end(id, json!({ "completed": true }))));
        let mut tools: Vec<(String, OpenCall)> = self.tools.drain().collect();
        tools.sort_by(|a, b| a.0.cmp(&b.0));
        out.extend(tools.into_iter().map(|(id, call)| agui::tool_end(&id, &Value::Object(call.args))));
        let mut children: Vec<String> = self.children.drain().filter(|(_, finished)| !finished).map(|(id, _)| id).collect();
        children.sort();
        out.extend(children.iter().map(|id| agui::subagent_finished(id)));
        let error = matches!(state, TurnState::Failed)
            .then(|| self.error.take().unwrap_or_else(|| ("turn_failed".into(), "The turn failed".into())));
        let failed = |run: &str| error.as_ref().map(|(code, message)| agui::run_error(thread, run, message, Some(code.as_str())));
        // A request still waiting (a child agent's) keeps its pause past the turn.
        if let Some(paused) = self.paused_run.take_if(|_| self.requests.is_empty()) {
            // The turn ended while it waited, such as on Stop or a failure.
            out.push(failed(&paused).unwrap_or_else(|| agui::run_finished(thread, &paused, Some(agui::cancelled()), None)));
        }
        if self.open_run.is_none() && self.next_run.is_some() {
            // The client is waiting on the run it asked for.
            self.open(out, thread, sequence);
        }
        if let Some(run) = self.open_run.take() {
            let usage = self.usage.take();
            out.push(failed(&run).unwrap_or_else(|| match state {
                TurnState::Interrupted => agui::run_finished(thread, &run, Some(agui::cancelled()), usage),
                _ => agui::run_finished(thread, &run, None, usage),
            }));
        }
        self.next_run = None;
        self.next_parent = None;
        self.next_user = None;
        self.usage = None;
        self.error = None;
        self.ended.clear();
    }

    /// One `RUN_FINISHED` carrying every open request, bound to the paused run.
    fn pause_chunk(&self, thread: &str) -> Option<Value> {
        let run = self.paused_run.as_deref().filter(|_| !self.requests.is_empty())?;
        let interrupts = self.requests.iter()
            .map(|request| agui::bound_interrupt(&request.id, request.reason, &request.message, &request.schema,
                run, request.generation, request.metadata.clone()))
            .collect();
        Some(agui::run_finished(thread, run, Some(agui::interrupted(interrupts)), None))
    }
}

#[cfg(test)]
#[path = "agui_projection_tests.rs"]
mod tests;
