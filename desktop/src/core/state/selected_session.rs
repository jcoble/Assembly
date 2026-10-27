//! `SelectedSession` owns which session the window shows. The session view (g0-session) reads it and subscribes to
//! that session's rows.

use gpui_kit::*;

pub struct SelectedSession {
    session_id: Option<String>,
}

impl SelectedSession {
    pub fn new() -> Self {
        Self { session_id: None }
    }

    pub fn session_id(&self) -> Option<&str> {
        self.session_id.as_deref()
    }

    pub fn select(&mut self, _session_id: Option<String>, _cx: &mut Context<Self>) {
        todo!()
    }
}
