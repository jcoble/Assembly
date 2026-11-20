//! The actual owning Engine key owns every draft. Saved routes are separate transport metadata.
use super::Composer;
use crate::core::{engine::SessionKey, socket::row_source::ExpectedRowSource};
use crate::features::session::composer::attachment::AttachmentState;
use gpui_kit::{Context, Window};
/// The session picked while its open is still on the way; its owner is known once the open has found it.
pub(super) struct Parked {
    id: String,
    key: Option<SessionKey>,
    /// The owner's saved draft is in the box. A pick that names only the session never guesses one by id, since the
    /// same id can be another owner's; what is typed then joins the owner's draft once the open lands.
    loaded: bool,
}
impl Parked {
    /// The session it was picked for, once its owner is known.
    pub(super) fn key(&self) -> Option<SessionKey> {
        self.key.clone()
    }
    /// `key`'s saved draft came with its open: what the box shows for it was typed in this run, so it joins that.
    pub(super) fn saved_arrived(&mut self, key: &SessionKey) {
        if self
            .key
            .as_ref()
            .map_or(self.id == key.id, |own| own == key)
        {
            self.loaded = false;
        }
    }
}
/// `later` after `earlier`, a line apart when both have text.
pub(super) fn joined(earlier: String, later: String) -> String {
    [earlier, later]
        .into_iter()
        .filter(|part| !part.is_empty())
        .collect::<Vec<_>>()
        .join("\n")
}
impl Composer {
    pub fn set_target(
        &mut self,
        key: Option<SessionKey>,
        source: Option<ExpectedRowSource>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if key
            .as_ref()
            .zip(source.as_ref())
            .is_some_and(|(key, source)| key.machine != source.machine)
            || key.is_some() != source.is_some()
        {
            self.invalidate_source(cx);
            return;
        }
        if self.parked.is_none()
            && self.session_key == key
            && self.expected_source == source
            && (key.is_none() || self.source_valid)
        {
            return;
        }
        self.invalidate_source(cx);
        // A parked draft was typed for the picked session, whose own draft the old one's was already put aside for.
        match self.parked.take() {
            Some(parked) => {
                let loaded = parked.loaded;
                let left = self.parked_key(parked, key.as_ref());
                self.keep(left, !loaded, cx);
            }
            None => {
                if let Some(left) = self.session_key.take() {
                    self.keep(left, false, cx);
                }
            }
        }
        let (text, mut attachments) = key
            .as_ref()
            .and_then(|key| self.drafts.remove(key))
            .unwrap_or_default();
        if let Some(failed) = key.as_ref().and_then(|key| self.failed_drafts.get(key))
            && failed.0 == text
            && attachments.is_empty()
        {
            attachments = self
                .failed_drafts
                .remove(key.as_ref().expect("failed key"))
                .expect("existing failed draft")
                .1;
        }
        self.session = key.as_ref().map(|key| key.id.clone());
        self.machine = key.as_ref().map(|key| key.machine.clone());
        self.session_key = key;
        self.expected_source = source;
        self.source_valid = self.session_key.is_some();
        self.show_draft(text, window, cx);
        self.attachments = attachments;
        self.slash_selected = None;
        self.owner = None;
        self.advertised_target = None;
        self.config = None;
        self.error = None;
        self.running = None;
        self.meter = None;
        self.branch = None;
        self.picking = 0;
        self.has_opened = false;
        self.turn_settled = false;
        self._settle = None;
        self.locked = None;
        self.agent_name = None;
        self.read_can_steer(cx);
        self.read_config(cx);
        self.read_pending(cx);
        cx.notify();
    }
    /// Another session was picked and its open is on the way: what is typed from now on is that session's draft, and
    /// nothing sends until the open lands (`set_target`). Picking the session still bound brings its own draft back.
    /// A rail pick names only the session; its owner comes when the open finds it (`parked_owner`). A composer with no
    /// session yet parks too, with no draft of its own to put aside.
    pub fn park(
        &mut self,
        id: String,
        key: Option<SessionKey>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let bound = self.session_key.clone();
        let showing = self.parked.as_ref().map(|parked| &parked.id);
        if showing.or(bound.as_ref().map(|bound| &bound.id)) == Some(&id) {
            if let Some(key) = key {
                self.parked_owner(key);
            }
            return;
        }
        if bound.as_ref().is_some_and(|bound| bound.id == id) {
            self.unpark(window, cx);
            return;
        }
        match (self.parked.take(), bound) {
            (Some(parked), _) => {
                let loaded = parked.loaded;
                let left = self.parked_key(parked, None);
                self.keep(left, !loaded, cx);
            }
            (None, Some(bound)) => {
                self.invalidate_source(cx);
                self.keep(bound, false, cx);
            }
            (None, None) => {}
        }
        let (text, attachments) = key
            .as_ref()
            .and_then(|key| self.drafts.remove(key))
            .unwrap_or_default();
        let loaded = key.is_some();
        self.parked = Some(Parked { id, key, loaded });
        self.show_draft(text, window, cx);
        self.attachments = attachments;
        self.slash_selected = None;
        cx.notify();
    }
    /// Shows `text` in the box with the caret at its end, as a textarea given a new value has it, so keys typed next
    /// follow it.
    fn show_draft(&mut self, text: String, window: &mut Window, cx: &mut Context<Self>) {
        let end = text.len();
        self.draft.update(cx, |draft, cx| {
            draft.set_value(text, window, cx);
            draft.set_selected_range(end..end, cx);
        });
    }
    /// Puts the box's draft away under `key`; `join` adds it after a draft already saved there instead of replacing it.
    fn keep(&mut self, key: SessionKey, join: bool, cx: &mut Context<Self>) {
        let mut text = self.draft.read(cx).value().to_string();
        let mut attachments = std::mem::take(&mut self.attachments);
        if join && let Some((saved, mut kept)) = self.drafts.remove(&key) {
            text = joined(saved, text);
            kept.append(&mut attachments);
            attachments = kept;
        }
        self.drafts.insert(key.clone(), (text, attachments));
        self.save_kept(&key, cx);
    }
    /// The open found the picked session's owner.
    pub fn parked_owner(&mut self, key: SessionKey) {
        if let Some(parked) = self.parked.as_mut().filter(|parked| parked.id == key.id) {
            parked.key = Some(key);
        }
    }
    /// Where a parked draft is kept: under its session's owner, else the one the open lands on, else the bound one's.
    pub(super) fn parked_key(&self, parked: Parked, landing: Option<&SessionKey>) -> SessionKey {
        parked
            .key
            .or_else(|| landing.filter(|key| key.id == parked.id).cloned())
            .unwrap_or_else(|| SessionKey {
                machine: self.machine.clone().unwrap_or_default(),
                id: parked.id,
            })
    }
    /// The picked session's open didn't land: what was typed stays its draft, and the bound session comes back.
    /// The session keeps its owner, which only an open that lands names again, so its settings come back.
    pub fn unpark(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.parked.is_none() {
            return;
        }
        let owner = self.owner.clone();
        self.set_target(
            self.session_key.clone(),
            self.expected_source.clone(),
            window,
            cx,
        );
        if let Some((agent, machine, route)) = owner {
            self.set_owner(Some(agent), machine, route, cx);
        }
    }
    /// Not an error of this composer's: the pane owning the session decides what to say and reopens it.
    pub(super) fn source_lost(&mut self, cx: &mut Context<Self>) {
        let lost = self.session_key.clone();
        self.invalidate_source(cx);
        if let Some(key) = lost {
            cx.emit(super::ComposerEvent::SourceLost(key));
        }
    }

    pub fn invalidate_source(&mut self, cx: &mut Context<Self>) {
        if self.start_draft.is_some() {
            cx.notify();
            return;
        }
        self.source_valid = false;
        self.source_serial += 1;
        self.target_generation += 1;
        self._target = None;
        self._warm = None;
        self._config = None;
        self._pending_read = None;
        self.message_subscriptions.clear();
        self.uploads.clear();
        self.config = None;
        self.picking = 0;
        for attachment in &mut self.attachments {
            if matches!(attachment.state, AttachmentState::Uploading) {
                attachment.state = AttachmentState::Failed(
                    "Upload cancelled when the session source changed.".into(),
                );
            }
        }
        cx.notify();
    }
    pub(super) fn source_args(&self, mut args: serde_json::Value) -> serde_json::Value {
        if let Some(source) = &self.expected_source {
            args["expectedSource"] = serde_json::json!(source);
        }
        args
    }
    pub(super) fn source_refused(
        &mut self,
        error: &crate::features::session::client::session_client::CallError,
        cx: &mut Context<Self>,
    ) {
        if matches!(error,crate::features::session::client::session_client::CallError::Refused{code,..} if code=="source_changed")
        {
            self.invalidate_source(cx);
        }
    }
    pub(super) fn operational(&self) -> bool {
        self.source_valid && self.session_key.is_some()
    }
    /// Legacy unit fixture setup names its simulated owner explicitly; production uses set_target.
    #[cfg(test)]
    pub fn set_session(
        &mut self,
        session: Option<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let key = session.map(|id| SessionKey {
            machine: self.machine.clone().unwrap_or_else(|| "owner".into()),
            id,
        });
        let source = key.as_ref().map(|key| ExpectedRowSource {
            machine: key.machine.clone(),
            generation: 0,
            route_machine: None,
        });
        self.set_target(key, source, window, cx);
    }
}
