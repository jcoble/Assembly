//! `Composer` owns the draft (a multi-line kit input: Enter sends, Shift-Enter adds a line), the attached images, the
//! agent's config (`config/get`, read again when a turn ends) and the "/" menu's state. Send is `send/send` with the
//! draft, its "Attached image:" lines and a client id made here, which the session's user row echoes: until it does,
//! the timeline shows the prompt as pending. While a turn runs, a send steers it and Stop (`send/stop`) shows beside
//! it. Dropped or pasted images attach; any other dropped file
//! goes into the draft as its path. The draft lives here while the window runs. It draws, so it never reads a file: `Upload` does. Replaces Composer.svelte.

use std::rc::Rc;

use gpui_kit::component::input::TextareaState;
use gpui_kit::*;

use super::agent_config::AgentConfig;
use super::attachment::Attachment;
use crate::features::session::client::session_client::SessionClient;

pub struct Composer {
    client: Rc<dyn SessionClient>,
    session: Option<String>,
    draft: Entity<TextareaState>,
    attachments: Vec<Attachment>,
    config: Option<AgentConfig>,
    /// The "/" menu's highlighted row, or `None` while it's closed or Esc hid it.
    slash_selected: Option<usize>,
    /// The run going now, which Stop stops.
    running: Option<String>,
    _tasks: Vec<Task<()>>,
}

/// What the composer tells the session pane.
pub enum ComposerEvent {
    /// A send went out with this client id: the timeline shows it pending and scrolls to it.
    Sending { client_id: String, text: String },
    /// It answered its run id.
    Sent { client_id: String, run_id: String },
}

impl Composer {
    pub fn new(_client: Rc<dyn SessionClient>, _window: &mut Window, _cx: &mut Context<Self>) -> Self {
        todo!()
    }

    /// Points the composer at a session, with its saved draft.
    pub fn set_session(&mut self, _session: Option<String>, _window: &mut Window, _cx: &mut Context<Self>) {
        todo!()
    }

    pub fn set_running(&mut self, _run: Option<String>, _cx: &mut Context<Self>) {
        todo!()
    }

    /// Sends the draft once every attachment has uploaded.
    fn send(&mut self, _window: &mut Window, _cx: &mut Context<Self>) {
        todo!()
    }
}

impl EventEmitter<ComposerEvent> for Composer {}

impl Render for Composer {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
