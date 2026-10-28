//! `Composer` owns the draft (a multi-line kit input: Enter sends, Shift-Enter adds a line) and each session's unsent
//! draft while the window runs. Send is `send/send` with the draft and a clientId, which a retry of the same text
//! reuses. While a turn runs, a send steers it and a Stop button beside the draft stops it (`send/stop` with the run
//! id). The ring beside Send shows how much of the agent's context window is left (context_ring.rs), and the agent's
//! own menus and "/" commands come from its config (composer_config.rs). Images attach by drop, paste or the picker
//! and upload at once (composer_attach.rs); a send waits until each has uploaded. A sent prompt isn't shown as pending
//! before its row arrives. Drawn in composer_render.rs. Replaces Composer.svelte.

use std::collections::HashMap;
use std::rc::Rc;

use gpui_kit::component::input::{InputEvent, TextareaState};
use gpui_kit::*;
use serde_json::json;

use super::agent_config::AgentConfig;
use super::attachment::{Attachment, AttachmentState};
use super::context_meter::ContextMeter;
use crate::features::session::client::session_client::SessionClient;

pub struct Composer {
    client: Rc<dyn SessionClient>,
    session: Option<String>,
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
    /// A failed send's text and clientId, by session: sending that text again reuses the id, so if the first did reach
    /// the agent the backend answers its run instead of starting another.
    retry_ids: HashMap<String, (String, String)>,
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
    /// A send went out: the timeline puts its prompt at the top once it arrives.
    Sending,
    /// It answered its run id, for the session it was sent to.
    Sent { session: String, run_id: String },
}

impl Composer {
    pub fn new(client: Rc<dyn SessionClient>, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let draft = cx.new(|cx| {
            TextareaState::new(window, cx)
                .auto_grow(1, DRAFT_ROWS)
                .submit_on_enter(true)
                .placeholder("Message")
        });
        let enter = cx.subscribe_in(
            &draft,
            window,
            |composer, _, event, window, cx| match event {
                InputEvent::PressEnter { shift: false, .. } => composer.send(window, cx),
                InputEvent::Change => composer.draft_changed(cx),
                _ => {}
            },
        );
        Composer {
            client,
            session: None,
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
            retry_ids: HashMap::new(),
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
        self.draft
            .update(cx, |draft, cx| draft.set_value(text, window, cx));
        self.session = session;
        self.attachments.clear();
        self.uploads.clear();
        self.slash_selected = None;
        (self.config, self.error, self._config) = (None, None, None);
        self.read_config(cx);
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
        cx.emit(ComposerEvent::Sending);
        let client_id = match self.retry_ids.remove(&session) {
            Some((was, id)) if was == text => id,
            _ => {
                self.named += 1;
                let ms = chrono::Utc::now().timestamp_millis();
                format!("{ms}-{}-{}", std::process::id(), self.named)
            }
        };
        let args = json!({ "session": session, "text": text, "clientId": client_id });
        let call = self.client.call("send", "send", args, None, cx);
        self._tasks.retain(|task| !task.is_ready());
        self._tasks
            .push(cx.spawn_in(window, async move |composer, cx| {
                let answer = call.await;
                composer
                    .update_in(cx, |composer, window, cx| match answer {
                        Ok(answer) => {
                            if let Some(run_id) = answer["runId"].as_str() {
                                cx.emit(ComposerEvent::Sent {
                                    session,
                                    run_id: run_id.to_string(),
                                });
                            }
                        }
                        // It didn't go out, or the socket closed before it answered (`Aborted`: a send has no key,
                        // so nothing else takes its place): it comes back to its session's draft with its images,
                        // unless something new was typed there, and a resend keeps its clientId in case the first
                        // did land. Images don't follow a draft to another session.
                        Err(_) => {
                            composer
                                .retry_ids
                                .insert(session.clone(), (text, client_id));
                            if composer.session.as_ref() != Some(&session) {
                                composer.drafts.entry(session).or_insert(typed);
                            } else if composer.draft.read(cx).value().is_empty()
                                && composer.attachments.is_empty()
                            {
                                composer
                                    .draft
                                    .update(cx, |draft, cx| draft.set_value(typed, window, cx));
                                composer.attachments = attached;
                                cx.notify();
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

#[cfg(test)]
#[path = "composer_view_tests.rs"]
mod tests;
