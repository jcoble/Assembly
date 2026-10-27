//! `Composer` owns the draft (a multi-line kit input: Enter sends, Shift-Enter adds a line), the attached images, the
//! agent's config (`config/get`, read again when a turn ends) and the "/" menu's state. Send is `send/send` with the
//! draft and its "Attached image:" lines. (A pending prompt waits on the base lane echoing a client id on the user
//! row.) While a turn runs, a send
//! steers it and Send turns into Stop (`send/stop`). Dropped or pasted images attach; any other dropped file goes
//! into the draft as its path. Each session's unsent draft lives here while the window runs. It draws, so it never
//! reads a file: `upload_image` does. Replaces Composer.svelte.

use std::collections::HashMap;
use std::rc::Rc;

use gpui_kit::component::input::{InputEvent, Textarea, TextareaState};
use gpui_kit::*;
use serde_json::json;

use super::agent_config::AgentConfig;
use super::attachment::{Attachment, AttachmentState};
use crate::features::session::client::session_client::{CallError, SessionClient};

pub struct Composer {
    client: Rc<dyn SessionClient>,
    session: Option<String>,
    draft: Entity<TextareaState>,
    /// The other sessions' unsent drafts, by session id.
    drafts: HashMap<String, String>,
    attachments: Vec<Attachment>,
    config: Option<AgentConfig>,
    /// The "/" menu's highlighted row, or `None` while it's closed or Esc hid it.
    slash_selected: Option<usize>,
    /// The run going now, which Stop stops.
    running: Option<String>,
    _enter: Subscription,
    _tasks: Vec<Task<()>>,
}

/// What the composer tells the session pane.
pub enum ComposerEvent {
    /// A send went out with this text: the timeline puts its prompt at the top once it arrives.
    Sending { text: String },
    /// It answered its run id.
    Sent { run_id: String },
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
            config: None,
            slash_selected: None,
            running: None,
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
        cx.emit(ComposerEvent::Sending { text: text.clone() });
        let call = self.client.call(
            "send",
            "send",
            json!({ "session": session, "text": text }),
            None,
        );
        self._tasks
            .push(cx.spawn_in(window, async move |composer, cx| {
                let answer = call.await;
                composer
                    .update_in(cx, |composer, window, cx| match answer {
                        Ok(answer) => {
                            if let Some(run_id) = answer["runId"].as_str() {
                                cx.emit(ComposerEvent::Sent {
                                    run_id: run_id.to_string(),
                                });
                            }
                        }
                        Err(CallError::Aborted) => {}
                        // The prompt didn't go out: it comes back to the draft, unless something new was typed.
                        Err(_) if composer.draft.read(cx).value().is_empty() => {
                            composer
                                .draft
                                .update(cx, |draft, cx| draft.set_value(typed, window, cx));
                        }
                        Err(_) => {}
                    })
                    .ok();
            }));
    }
}

/// The most lines the draft grows to before it scrolls.
const DRAFT_ROWS: usize = 10;

impl EventEmitter<ComposerEvent> for Composer {}

impl Render for Composer {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div().w_full().child(Textarea::new(&self.draft))
    }
}

#[cfg(test)]
#[path = "composer_view_tests.rs"]
mod tests;
