//! `Composer` owns the draft (a multi-line kit input: Enter sends, Shift-Enter adds a line) and each session's unsent
//! draft while the window runs. Send is `send/send` with the draft and a clientId, which a retry of the same text
//! reuses. While a turn runs, a send steers it and a Stop button beside the draft stops it (`send/stop` with the run
//! id). The ring beside Send shows how much of the agent's context window is left (context_ring.rs), and the agent's
//! own menus and "/" commands come from its config (composer_config.rs). Images attach by drop, paste or the picker
//! and upload at once (composer_attach.rs); a send waits until each has uploaded. A prompt stays pending until its
//! own pushed user row arrives. Drawn in composer_render.rs. Replaces Composer.svelte.

use std::collections::HashMap;
use std::rc::Rc;

use gpui_kit::component::input::{InputEvent, TextareaState};
use gpui_kit::*;
use serde_json::json;

use super::agent_config::AgentConfig;
use super::attachment::{Attachment, AttachmentState};
use super::context_meter::ContextMeter;
use crate::features::session::client::session_client::{CallError, SessionClient};

pub struct Composer {
    client: Rc<dyn SessionClient>,
    session: Option<String>,
    /// Identifies this visit, including leaving and returning to the same session.
    target_generation: u64,
    owner: Option<(String, Option<String>, String)>,
    advertised_target: Option<String>,
    backend_connected: bool,
    _target: Option<Task<()>>,
    _warm: Option<Task<()>>,
    _connection: Option<Subscription>,
    draft: Entity<TextareaState>,
    /// The other sessions' unsent drafts, by session id.
    drafts: HashMap<String, String>,
    attachments: Vec<Attachment>,
    /// Each attachment's upload while it goes, by its id; a chip removed drops its upload.
    uploads: HashMap<u64, Task<()>>,
    /// How many attachments this composer has named, for the next id.
    attached: u64,
    /// The "/" menu's highlighted row, or `None` while it's closed or Esc hid it.
    slash_selected: Option<usize>,
    /// Keeps the highlighted row in sight as the keys move it.
    slash_scroll: ScrollHandle,
    /// The agent's menus and commands for this session, once read.
    config: Option<AgentConfig>,
    /// Why the last pick failed, above the box until the next works or the session changes.
    error: Option<SharedString>,
    /// The run going now, which Stop stops.
    running: Option<String>,
    /// The open session's machine while it isn't answering: Send and Stop wait for it.
    offline: Option<String>,
    /// Connect's `machines/connect` while it goes.
    connecting: Option<Task<()>>,
    /// Connect answered and the machine still didn't, until it answers or the session's machine changes.
    still_offline: bool,
    /// How full the agent's context window is, from the session's newest report.
    meter: Option<ContextMeter>,
    /// The branch the session's newest turn started on, shown in the footer; `None` before its first turn.
    branch: Option<String>,
    /// The open session has messages: the draft's hint asks for follow-up changes.
    messages: bool,
    /// The draft's hint as last set (`placeholder`).
    hint: &'static str,
    /// A failed send's immutable request: a retry reuses its original receipt identity.
    retry_ids: HashMap<String, PromptRequest>,
    pending: HashMap<(Option<String>, String), Vec<PendingPrompt>>,
    _pending_read: Option<Task<()>>,
    message_subscriptions: HashMap<String, Subscription>,
    machine: Option<String>,
    failed_drafts: HashMap<String, (String, Vec<Attachment>)>,
    delivered: HashMap<(Option<String>, String), std::collections::HashSet<String>>,
    /// How many sends this composer has named, for the next clientId.
    named: u64,
    /// Counts each pick's start and answer; a config read that saw it change answers what came before.
    picks: u64,
    _enter: Subscription,
    /// The config read going now.
    _config: Option<Task<()>>,
    /// Sends and Stops still going; finished ones are let go at the next.
    _tasks: Vec<Task<()>>,
}

/// What the composer tells the session pane.
pub enum ComposerEvent {
    /// A local pending prompt was captured before the call.
    Sending,
    /// The owner committed the prompt. Its provider turn arrives later through rows.
    Accepted(OwnerPromptAccepted),
    PendingChanged,
}
#[path = "owner_prompt.rs"]
mod owner_prompt;
pub use owner_prompt::OwnerPromptAccepted;
use owner_prompt::{PendingPrompt, PromptRequest};

/// The draft's hint: "Message" for a new session's first message, the mock's words once it has messages.
pub fn placeholder(messages: bool) -> &'static str {
    if messages {
        "Ask for follow-up changes…"
    } else {
        "Message"
    }
}

impl Composer {
    pub fn new(client: Rc<dyn SessionClient>, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let draft = cx.new(|cx| {
            TextareaState::new(window, cx)
                .auto_grow(1, DRAFT_ROWS)
                .submit_on_enter(true)
                .placeholder(placeholder(false))
        });
        let enter = cx.subscribe_in(
            &draft,
            window,
            |composer, _, event, window, cx| match event {
                InputEvent::PressEnter { shift: false, .. } => composer.send(window, cx),
                InputEvent::Change => composer.draft_changed(cx),
                // The box's edge shows only while the draft has focus.
                InputEvent::Focus | InputEvent::Blur => cx.notify(),
                _ => {}
            },
        );
        let backend_connected = crate::core::socket::client::SocketClient::try_global(cx)
            .map(|socket| {
                matches!(
                    socket.read(cx).state(),
                    crate::core::socket::connection_state::ConnectionState::Connected { .. }
                )
            })
            .unwrap_or(true);
        let connection = crate::core::socket::client::SocketClient::try_global(cx).map(|socket| {
            cx.observe(&socket, |composer: &mut Self, socket, cx| {
                let connected = matches!(
                    socket.read(cx).state(),
                    crate::core::socket::connection_state::ConnectionState::Connected { .. }
                );
                composer.connection_changed(connected, cx);
            })
        });
        Composer {
            client,
            session: None,
            target_generation: 0,
            owner: None,
            advertised_target: None,
            backend_connected,
            _target: None,
            _warm: None,
            _connection: connection,
            draft,
            drafts: HashMap::new(),
            attachments: Vec::new(),
            uploads: HashMap::new(),
            attached: 0,
            slash_selected: None,
            slash_scroll: ScrollHandle::new(),
            config: None,
            error: None,
            running: None,
            offline: None,
            connecting: None,
            still_offline: false,
            meter: None,
            branch: None,
            messages: false,
            hint: placeholder(false),
            retry_ids: HashMap::new(),
            pending: HashMap::new(),
            _pending_read: None,
            message_subscriptions: HashMap::new(),
            machine: None,
            failed_drafts: HashMap::new(),
            delivered: HashMap::new(),
            named: 0,
            picks: 0,
            _enter: enter,
            _config: None,
            _tasks: Vec::new(),
        }
    }

    /// Points the composer at a session: keeps the draft it leaves and puts back the one it left there.
    pub fn set_session(
        &mut self,
        session: Option<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.session == session {
            return;
        }
        self.target_generation += 1;
        self.owner = None;
        self.advertised_target = None;
        self._target = None;
        self._warm = None;
        if let Some(left) = self.session.take() {
            let text = self.draft.read(cx).value().to_string();
            if !text.is_empty() {
                self.drafts.insert(left, text);
            }
        }
        let text = session
            .as_ref()
            .and_then(|session| self.drafts.remove(session))
            .unwrap_or_default();
        let restored = session.as_ref().and_then(|session| {
            if self
                .failed_drafts
                .get(session)
                .is_some_and(|(typed, _)| typed == &text)
            {
                self.failed_drafts
                    .remove(session)
                    .map(|(_, attachments)| attachments)
            } else {
                None
            }
        });
        self.draft
            .update(cx, |draft, cx| draft.set_value(text, window, cx));
        self.session = session;
        self.attachments = restored.unwrap_or_default();
        self.uploads.clear();
        self.slash_selected = None;
        (self.config, self.error, self._config) = (None, None, None);
        self.read_config(cx);
        self.read_pending(cx);
        cx.notify();
    }

    /// The run going now; when one ends, the agent's menus are read again.
    pub fn set_running(&mut self, run: Option<String>, cx: &mut Context<Self>) {
        if self.running != run {
            let ended = run.is_none();
            self.running = run;
            if ended {
                self.read_config(cx);
            }
            cx.notify();
        }
    }

    /// The newest context report as (used, size), or `None` before the agent sends one.
    pub fn set_context(&mut self, context: Option<(i64, i64)>, cx: &mut Context<Self>) {
        let meter = context.and_then(|(used, size)| ContextMeter::of(used, size));
        if self.meter != meter {
            self.meter = meter;
            cx.notify();
        }
    }

    /// The branch the newest turn started on (model/turn_line.rs `latest_branch`).
    pub fn set_branch(&mut self, branch: Option<String>, cx: &mut Context<Self>) {
        if self.branch != branch {
            self.branch = branch;
            cx.notify();
        }
    }

    /// Whether the open session has messages, for the draft's hint (`placeholder`).
    pub fn set_messages(&mut self, messages: bool, cx: &mut Context<Self>) {
        if self.messages != messages {
            self.messages = messages;
            cx.notify();
        }
    }

    #[cfg(test)]
    pub fn meter(&self) -> Option<&ContextMeter> {
        self.meter.as_ref()
    }

    /// Every attached image has uploaded.
    fn all_saved(&self) -> bool {
        (self.attachments.iter())
            .all(|attachment| matches!(attachment.state, AttachmentState::Saved { .. }))
    }

    /// The open session's machine while it isn't answering, from the machines list; `None` once it answers.
    pub fn set_offline(&mut self, machine: Option<String>, cx: &mut Context<Self>) {
        if self.offline != machine {
            self.offline = machine;
            (self.connecting, self.still_offline) = (None, false);
            self.refresh_config_target(cx);
            cx.notify();
        }
    }

    /// Connect: asks the backend to reach the offline machine now (`machines/connect`, up to 10 seconds). One that
    /// answers comes back through the machines list; one that doesn't says so.
    fn connect(&mut self, cx: &mut Context<Self>) {
        let Some(machine) = self.offline.clone().filter(|_| self.connecting.is_none()) else {
            return;
        };
        let call = (self.client).call(
            "machines",
            "connect",
            json!({ "machine": machine }),
            None,
            cx,
        );
        self.connecting = Some(cx.spawn(async move |composer, cx| {
            _ = call.await;
            _ = composer.update(cx, |composer, cx| {
                composer.connecting = None;
                composer.still_offline = composer.offline.is_some();
                cx.notify();
            });
        }));
        cx.notify();
    }

    /// Stops the run going now. It goes on if the call fails, so Stop stays to be pressed again.
    fn stop(&mut self, cx: &mut Context<Self>) {
        let Some(run) = self.running.clone().filter(|_| self.offline.is_none()) else {
            return;
        };
        let call = self
            .client
            .call("send", "stop", json!({ "runId": run }), None, cx);
        self._tasks.retain(|task| !task.is_ready());
        self._tasks.push(cx.spawn(async move |_, _| _ = call.await));
    }

    /// Sends the draft once every attachment has uploaded; one whose upload failed is removed first.
    fn send(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(session) = self.session.clone() else {
            return;
        };
        let typed = self.draft.read(cx).value().trim().to_string();
        if self.offline.is_some()
            || !self.all_saved()
            || (typed.is_empty() && self.attachments.is_empty())
        {
            return;
        }
        let text = Attachment::with_lines(&typed, &self.attachments);
        let attached = std::mem::take(&mut self.attachments);
        self.uploads.clear();
        (self.slash_selected, self.error) = (None, None);
        self.draft
            .update(cx, |draft, cx| draft.set_value("", window, cx));
        let attachment_ids: Vec<String> = attached
            .iter()
            .filter_map(|attachment| match &attachment.state {
                AttachmentState::Saved { path } => Some(path.clone()),
                _ => None,
            })
            .collect();
        let request = match self.retry_ids.remove(&session) {
            Some(request) if request.text == text && request.attachment_ids == attachment_ids => {
                request
            }
            _ => {
                self.named += 1;
                let ms = chrono::Utc::now().timestamp_millis();
                PromptRequest {
                    text: text.clone(),
                    client_id: format!("{ms}-{}-{}", std::process::id(), self.named),
                    attachment_ids,
                    epoch: None,
                }
            }
        };
        self.submit(session, request, typed, attached, window, cx);
    }

    /// Retries a failed prompt's captured payload while preserving any new draft.
    pub fn retry_pending(&mut self, client_id: &str, window: &mut Window, cx: &mut Context<Self>) {
        let Some(session) = self.session.clone().filter(|_| self.offline.is_none()) else {
            return;
        };
        let Some(pending) = self
            .pending(&session)
            .iter()
            .find(|pending| {
                pending.client_id() == client_id && pending.state.starts_with("Failed:")
            })
            .cloned()
        else {
            return;
        };
        let (typed, references) = Attachment::split_lines(pending.text());
        let mut attached = references
            .into_iter()
            .map(|(path, name, _)| {
                self.attached += 1;
                Attachment {
                    id: self.attached,
                    name: name.into(),
                    size: 0,
                    state: AttachmentState::Saved { path },
                }
            })
            .collect();
        if self.draft.read(cx).value().to_string() == typed
            && self
                .attachments
                .iter()
                .filter_map(|a| match &a.state {
                    AttachmentState::Saved { path } => Some(path),
                    _ => None,
                })
                .eq(pending.request.attachment_ids.iter())
        {
            self.draft
                .update(cx, |draft, cx| draft.set_value("", window, cx));
            attached = std::mem::take(&mut self.attachments);
        } else if self
            .failed_drafts
            .get(&session)
            .is_some_and(|(text, _)| text == &typed)
        {
            attached = self.failed_drafts.remove(&session).unwrap().1;
        }
        self.error = None;
        self.retry_ids.remove(&session);
        self.submit(session, pending.request, typed, attached, window, cx);
    }

    pub fn recover_pending(&mut self, cx: &mut Context<Self>) {
        self.read_pending(cx);
    }

    fn submit(
        &mut self,
        session: String,
        request: PromptRequest,
        typed: String,
        attached: Vec<Attachment>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let args = json!({ "session": session, "text": request.text,
            "clientId": request.client_id, "attachmentIds": request.attachment_ids,
            "epoch": request.epoch });
        let prompt_key = (self.machine.clone(), session.clone());
        let pending = self.pending.entry(prompt_key.clone()).or_default();
        if !pending
            .iter()
            .any(|p| p.request.client_id == request.client_id)
        {
            pending.push(PendingPrompt::sending(request.clone()));
        } else if let Some(pending) = pending
            .iter_mut()
            .find(|p| p.request.client_id == request.client_id)
        {
            pending.state = "sending".into();
        }
        cx.emit(ComposerEvent::Sending);
        let call = self.client.call("send", "send", args, None, cx);
        self._tasks.retain(|task| !task.is_ready());
        self._tasks
            .push(cx.spawn_in(window, async move |composer, cx| {
                let answer = call.await.and_then(|answer| {
                    let accepted: OwnerPromptAccepted = serde_json::from_value(answer)
                        .map_err(|_| CallError::Failed("Invalid prompt acceptance".into()))?;
                    if accepted.session_id != session
                        || accepted.client_id != request.client_id
                        || accepted.message_id != format!("prompt/{session}/{}", request.client_id)
                        || accepted.state != "queued"
                        || accepted.run_id.is_some()
                        || request.epoch.is_some_and(|epoch| epoch != accepted.epoch)
                        || accepted.message_version < 0
                        || accepted.seq < 0
                        || accepted.epoch < 0
                    {
                        return Err(CallError::Failed("Invalid prompt acceptance".into()));
                    }
                    Ok(accepted)
                });
                composer
                    .update_in(cx, |composer, window, cx| match answer {
                        Ok(accepted) => {
                            if let Some(pending) =
                                composer.pending.get_mut(&prompt_key).and_then(|items| {
                                    items
                                        .iter_mut()
                                        .find(|p| p.request.client_id == accepted.client_id)
                                })
                            {
                                if pending.version <= accepted.message_version {
                                    pending.state = accepted.state.clone();
                                    pending.version = accepted.message_version;
                                }
                                pending.receipt = Some(accepted.clone());
                            }
                            if composer.session.as_ref() == Some(&session)
                                && composer.machine == prompt_key.0
                            {
                                composer.watch_pending(cx);
                            }
                            cx.emit(ComposerEvent::Accepted(accepted));
                        }
                        // It didn't go out, or the socket closed before it answered (`Aborted`: a send has no key,
                        // so nothing else takes its place): it comes back to its session's draft with its images,
                        // unless something new was typed there, and a resend keeps its clientId in case the first
                        // did land. Images don't follow a draft to another session.
                        Err(error) => {
                            let selected = composer.session.as_ref() == Some(&session)
                                && composer.machine == prompt_key.0;
                            if composer
                                .delivered
                                .get(&prompt_key)
                                .is_some_and(|ids| ids.contains(&request.client_id))
                            {
                                return;
                            }
                            if let Some(pending) =
                                composer.pending.get_mut(&prompt_key).and_then(|items| {
                                    items
                                        .iter_mut()
                                        .find(|p| p.request.client_id == request.client_id)
                                })
                            {
                                pending.state = format!("Failed: {}", error.reason());
                            }
                            cx.emit(ComposerEvent::PendingChanged);
                            if selected {
                                let hint = if matches!(error, CallError::Unreachable { .. }) {
                                    " Check Settings > Connections."
                                } else {
                                    ""
                                };
                                composer.error = Some(
                                    format!(
                                        "Couldn't send: {}.{hint}",
                                        error.reason().trim_end_matches('.')
                                    )
                                    .into(),
                                );
                                cx.notify();
                            }
                            composer.retry_ids.insert(session.clone(), request);
                            if !selected {
                                composer
                                    .drafts
                                    .entry(session.clone())
                                    .or_insert(typed.clone());
                                composer.failed_drafts.insert(session, (typed, attached));
                            } else if composer.draft.read(cx).value().is_empty()
                                && composer.attachments.is_empty()
                            {
                                composer
                                    .draft
                                    .update(cx, |draft, cx| draft.set_value(typed, window, cx));
                                composer.attachments = attached;
                                cx.notify();
                            } else {
                                composer.failed_drafts.insert(session, (typed, attached));
                            }
                        }
                    })
                    .ok();
            }));
    }
}

/// The most lines the draft grows to before it scrolls.
const DRAFT_ROWS: usize = 10;

impl EventEmitter<ComposerEvent> for Composer {}

#[path = "composer_attach.rs"]
mod attach;
#[path = "composer_config.rs"]
mod config;
#[path = "composer_render.rs"]
mod render;
#[path = "composer_target.rs"]
mod target;

#[cfg(test)]
#[path = "composer_view_tests.rs"]
mod tests;
