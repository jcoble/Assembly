//! `SelectedSession` owns which session the window shows. The session view (g0-session) reads it and subscribes to
//! that session's rows.

use gpui_kit::*;

pub struct SelectedSession {
    session_id: Option<String>,
}

struct GlobalSelected(Entity<SelectedSession>);

impl Global for GlobalSelected {}

impl SelectedSession {
    /// Makes the window's one selection, none at first. It runs once at app start.
    pub fn init(cx: &mut App) {
        let selected = cx.new(|_| SelectedSession::new());
        cx.set_global(GlobalSelected(selected));
    }

    pub fn global(cx: &App) -> Entity<Self> {
        cx.global::<GlobalSelected>().0.clone()
    }

    pub fn new() -> Self {
        Self { session_id: None }
    }

    pub fn session_id(&self) -> Option<&str> {
        self.session_id.as_deref()
    }

    pub fn select(&mut self, session_id: Option<String>, cx: &mut Context<Self>) {
        if self.session_id != session_id {
            self.session_id = session_id;
            cx.notify();
        }
    }
}
