//! The actual owning Engine key owns every draft. Saved routes are separate transport metadata.
use super::Composer;
use crate::core::{engine::SessionKey, socket::row_source::ExpectedRowSource};
use crate::features::session::composer::attachment::AttachmentState;
use gpui_kit::{Context, Window};
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
        if self.session_key == key
            && self.expected_source == source
            && (key.is_none() || self.source_valid)
        {
            return;
        }
        self.invalidate_source(cx);
        if let Some(left) = self.session_key.take() {
            let text = self.draft.read(cx).value().to_string();
            self.drafts
                .insert(left, (text, std::mem::take(&mut self.attachments)));
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
        self.draft
            .update(cx, |draft, cx| draft.set_value(text, window, cx));
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
impl Composer {
    /// Puts words in a session's draft, after anything already typed there, so its own Send sends them. A session not
    /// on screen keeps them until it is.
    pub fn stage_text(
        &mut self,
        key: SessionKey,
        text: &str,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let joined = |held: &str| {
            if held.trim().is_empty() {
                text.to_string()
            } else {
                format!("{}\n\n{text}", held.trim_end())
            }
        };
        if self.session_key.as_ref() == Some(&key) {
            let value = joined(&self.draft.read(cx).value());
            self.draft
                .update(cx, |draft, cx| draft.set_value(value, window, cx));
            self.draft_changed(cx);
        } else {
            let draft = self.drafts.entry(key).or_default();
            draft.0 = joined(&draft.0);
        }
        cx.notify();
    }
}
