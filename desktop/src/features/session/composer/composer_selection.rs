//! The actual owning Engine key owns every draft. Saved routes are separate transport metadata.
use gpui_kit::{Context,Window};
use crate::core::{engine::SessionKey,socket::row_source::ExpectedRowSource};
use super::Composer;
use crate::features::session::composer::attachment::AttachmentState;
impl Composer {
    pub fn set_target(&mut self,key:Option<SessionKey>,source:Option<ExpectedRowSource>,window:&mut Window,cx:&mut Context<Self>) {
        if key.as_ref().zip(source.as_ref()).is_some_and(|(key,source)|key.machine!=source.machine)
            || key.is_some()!=source.is_some() {self.invalidate_source(cx);return;}
        if self.session_key==key && self.expected_source==source && (key.is_none()||self.source_valid) {return;}
        self.invalidate_source(cx);
        if let Some(left)=self.session_key.take() {
            let text=self.draft.read(cx).value().to_string();
            self.drafts.insert(left,(text,std::mem::take(&mut self.attachments)));
        }
        let(text,mut attachments)=key.as_ref().and_then(|key|self.drafts.remove(key)).unwrap_or_default();
        if let Some(failed)=key.as_ref().and_then(|key|self.failed_drafts.get(key)) {
            if failed.0==text && attachments.is_empty() {
                attachments=self.failed_drafts.remove(key.as_ref().expect("failed key")).expect("existing failed draft").1;
            }
        }
        self.session=key.as_ref().map(|key|key.id.clone());
        self.machine=key.as_ref().map(|key|key.machine.clone());
        self.session_key=key;self.expected_source=source;self.source_valid=self.session_key.is_some();
        self.draft.update(cx,|draft,cx|draft.set_value(text,window,cx));
        self.attachments=attachments;self.slash_selected=None;self.owner=None;self.advertised_target=None;
        self.config=None;self.error=None;self.running=None;self.meter=None;self.branch=None;
        self.read_config(cx);self.read_pending(cx);cx.notify();
    }
    pub fn invalidate_source(&mut self,cx:&mut Context<Self>) {
        self.source_valid=false;self.source_serial+=1;self.target_generation+=1;
        self._target=None;self._warm=None;self._config=None;self._pending_read=None;
        self.message_subscriptions.clear();self.uploads.clear();self.config=None;
        for attachment in &mut self.attachments {
            if matches!(attachment.state,AttachmentState::Uploading) {
                attachment.state=AttachmentState::Failed("Upload cancelled when the session source changed.".into());
            }
        }
        cx.notify();
    }
    pub(super) fn operational(&self)->bool {self.source_valid&&self.session_key.is_some()}
    /// Legacy unit fixture setup names its simulated owner explicitly; production uses set_target.
    #[cfg(test)]
    pub fn set_session(&mut self,session:Option<String>,window:&mut Window,cx:&mut Context<Self>) {
        let key=session.map(|id|SessionKey{machine:self.machine.clone().unwrap_or_else(||"owner".into()),id});
        let source=key.as_ref().map(|key|ExpectedRowSource{machine:key.machine.clone(),generation:0,route_machine:None});
        self.set_target(key,source,window,cx);
    }
}
