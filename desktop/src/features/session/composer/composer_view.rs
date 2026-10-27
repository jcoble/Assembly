//! `Composer` owns the draft (a multi-line kit input: Enter sends, Shift-Enter adds a line) and each session's unsent
//! draft while the window runs. Send is `send/send` with the draft and a clientId, which a retry of the same text
//! reuses. While a turn runs, a send steers it and a Stop button beside the draft stops it (`send/stop` with the run
//! id). It holds the attachments and the "/" menu's state for when those are drawn; nothing
//! attaches images, draws the menus or draws the context ring yet, and a sent prompt isn't shown as pending before its
//! row arrives. Replaces Composer.svelte.

use std::collections::HashMap;
use std::rc::Rc;

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{InputEvent, Textarea, TextareaState};
use gpui_kit::component::{ActiveTheme as _, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::json;

use super::attachment::{Attachment, AttachmentState};
use crate::features::session::client::session_client::{CallError, SessionClient};

pub struct Composer {
    client: Rc<dyn SessionClient>,
    session: Option<String>,
    draft: Entity<TextareaState>,
    /// The other sessions' unsent drafts, by session id.
    drafts: HashMap<String, String>,
    attachments: Vec<Attachment>,
    /// The "/" menu's highlighted row, or `None` while it's closed or Esc hid it.
    slash_selected: Option<usize>,
    /// The run going now, which Stop stops.
    running: Option<String>,
    /// A failed send's text and clientId, by session: sending that text again reuses the id, so if the first did reach
    /// the agent the backend answers its run instead of starting another.
    retry_ids: HashMap<String, (String, String)>,
    /// How many sends this composer has named, for the next clientId.
    named: u64,
    _enter: Subscription,
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
        let enter = cx.subscribe_in(&draft, window, |composer, _, event, window, cx| {
            if let InputEvent::PressEnter { shift: false, .. } = event {
                composer.send(window, cx);
            }
        });
        Composer {
            client,
            session: None,
            draft,
            drafts: HashMap::new(),
            attachments: Vec::new(),
            slash_selected: None,
            running: None,
            retry_ids: HashMap::new(),
            named: 0,
            _enter: enter,
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
        self.slash_selected = None;
        cx.notify();
    }

    pub fn set_running(&mut self, run: Option<String>, cx: &mut Context<Self>) {
        if self.running != run {
            self.running = run;
            cx.notify();
        }
    }

    /// Stops the run going now. It goes on if the call fails, so Stop stays to be pressed again.
    fn stop(&mut self, cx: &mut Context<Self>) {
        let Some(run) = self.running.clone() else {
            return;
        };
        let call = self
            .client
            .call("send", "stop", json!({ "runId": run }), None, cx);
        self._tasks.retain(|task| !task.is_ready());
        self._tasks.push(cx.spawn(async move |_, _| _ = call.await));
    }

    /// Sends the draft once every attachment has uploaded.
    fn send(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(session) = self.session.clone() else {
            return;
        };
        let typed = self.draft.read(cx).value().trim().to_string();
        let uploading = self
            .attachments
            .iter()
            .any(|attachment| matches!(attachment.state, AttachmentState::Uploading));
        if uploading || (typed.is_empty() && self.attachments.is_empty()) {
            return;
        }
        let text = Attachment::with_lines(&typed, &self.attachments);
        self.attachments.clear();
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
                        Err(CallError::Aborted) => {}
                        // The prompt didn't go out: it comes back to its session's draft, unless something new was
                        // typed there.
                        Err(_) => {
                            composer
                                .retry_ids
                                .insert(session.clone(), (text, client_id));
                            if composer.session.as_ref() != Some(&session) {
                                composer.drafts.entry(session).or_insert(typed);
                            } else if composer.draft.read(cx).value().is_empty() {
                                composer
                                    .draft
                                    .update(cx, |draft, cx| draft.set_value(typed, window, cx));
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

impl Render for Composer {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let stop = cx.listener(|composer, _, _, cx| composer.stop(cx));
        let pill = cx.theme().radius_full();
        h_flex()
            .w_full()
            .gap_2()
            .items_end()
            .child(div().flex_1().child(Textarea::new(&self.draft)))
            .when(self.running.is_some(), |row| {
                row.child(
                    Button::new("composer:stop")
                        .primary()
                        .rounded(pill)
                        .label("Stop")
                        .on_click(stop),
                )
            })
    }
}

#[cfg(test)]
#[path = "composer_view_tests.rs"]
mod tests;
