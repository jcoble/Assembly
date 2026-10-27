//! `Composer` owns the draft (a multi-line kit input: Enter sends, Shift-Enter adds a line), the attached images and
//! the "/" menu's state. Send is `send/send` with the draft and its "Attached image:" lines; while a turn runs, a
//! send steers it and Stop (`send/stop`) shows beside it. Dropped or pasted images attach; any other dropped file
//! goes into the draft as its path. The draft is saved through the save lane g0-host provides on the backend clock;
//! this owns no timer. It draws, so it never reads a file: `Upload` does. Replaces Composer.svelte.

use gpui_kit::component::input::InputState;
use gpui_kit::*;

use super::attachment::Attachment;
use super::slash_menu::SlashCommand;

pub struct Composer {
    session: Option<String>,
    draft: Entity<InputState>,
    attachments: Vec<Attachment>,
    /// The agent's slash commands, as `config/get` last listed them.
    commands: Vec<SlashCommand>,
    /// The "/" menu's highlighted row, or `None` while it's closed or Esc hid it.
    slash_selected: Option<usize>,
    /// The run going now, which Stop stops.
    running: Option<String>,
    _tasks: Vec<Task<()>>,
}

/// What the composer tells the session pane.
pub enum ComposerEvent {
    /// A send went out and answered its run id: the timeline scrolls the prompt to the top.
    Sent { run_id: String },
}

impl Composer {
    pub fn new(_window: &mut Window, _cx: &mut Context<Self>) -> Self {
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
