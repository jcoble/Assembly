//! `SelectedSession` owns which session the window shows. The session view (g0-session) reads it and subscribes to
//! that session's rows.

use gpui_kit::*;

pub struct SelectedSession {
    session_id: Option<String>,
    key: Option<crate::core::engine::SessionKey>,
    read_only: bool,
    source:Option<crate::core::socket::row_source::ExpectedRowSource>,
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
        Self { session_id: None, key: None, read_only: false,source:None }
    }

    pub fn session_id(&self) -> Option<&str> {
        self.session_id.as_deref()
    }

    pub fn key(&self) -> Option<&crate::core::engine::SessionKey> { self.key.as_ref() }
    pub fn source(&self)->Option<&crate::core::socket::row_source::ExpectedRowSource>{self.source.as_ref()}
    pub fn read_only(&self) -> bool { self.read_only }
    /// Only the navigator's already prepared workspace commit changes qualified selection.
    pub(crate) fn commit_open(&mut self, key: crate::core::engine::SessionKey, source:crate::core::socket::row_source::ExpectedRowSource, read_only: bool, cx: &mut Context<Self>) {
        let changed = self.key.as_ref() != Some(&key) || self.read_only != read_only || self.source.as_ref()!=Some(&source);
        self.session_id = Some(key.id.clone()); self.key = Some(key); self.read_only = read_only;self.source=Some(source);
        if changed { cx.notify(); }
    }

    /// Fixture setup only; production selection is committed by the source-qualified navigator.
    #[cfg(test)]
    pub fn select(&mut self, session_id: Option<String>, cx: &mut Context<Self>) {
        if self.session_id != session_id || self.key.is_some() || self.source.is_some() {
            self.session_id = session_id;
            self.key = None;
            self.source=None;
            self.read_only = false;
            cx.notify();
        }
    }
}

impl EventEmitter<crate::core::intents::session_open::SessionOpenResult> for SelectedSession {}
