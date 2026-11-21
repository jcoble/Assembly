//! A session's unsent draft is saved on the machine it runs on (`drafts/set`, through its source) as it changes,
//! and comes back with the session's open (`sessions/open_target`), so it outlasts a restart, as the old app's did.
//! The `drafts` map stays this run's working copy: every change to it is saved, so after a restart the saved draft is
//! the only one there is. A session saves here only once its saved draft has come into this composer, so a save never
//! replaces text it hasn't seen. An older remote's open brings none, so its sessions never save.
use super::*;
use crate::core::engine::SessionKey;
use crate::core::socket::row_source::ExpectedRowSource;
use futures_channel::oneshot;
use serde::{Deserialize, Serialize};

/// A session's saved draft: its text and the images already saved for it.
#[derive(Clone, Debug, Default, PartialEq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SavedDraft {
    pub text: String,
    #[serde(default)]
    pub attachments: Vec<SavedImage>,
}

#[derive(Clone, Debug, PartialEq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SavedImage {
    pub name: String,
    pub size: u64,
    pub path: String,
}

/// One session's save going now; the newest draft changed meanwhile waits for it, replacing any older one.
pub(super) struct DraftSave {
    next: Option<SavedDraft>,
    _call: Task<()>,
}

impl Composer {
    /// The open brought `key`'s saved draft. The first one in this run joins in front of whatever this run already
    /// holds for the session (text typed while its open was on the way); a later one is older than this run's own.
    /// Saves go where the session runs, through `source`; each open names it again.
    pub fn offer_saved(
        &mut self,
        key: &SessionKey,
        saved: Option<SavedDraft>,
        source: ExpectedRowSource,
    ) {
        let Some(saved) = saved else {
            return;
        };
        if self.saved_drafts.insert(key.clone(), source).is_some() {
            return;
        }
        // What the box shows for the picked session was typed in this run: it joins the saved draft when the open lands.
        if let Some(parked) = self.parked.as_mut() {
            parked.saved_arrived(key);
        }
        if saved.text.is_empty() && saved.attachments.is_empty() {
            return;
        }
        let mut attachments: Vec<Attachment> = (saved.attachments.into_iter())
            .map(|image| {
                self.attached += 1;
                Attachment {
                    id: self.attached,
                    name: image.name.into(),
                    size: image.size,
                    state: AttachmentState::Saved { path: image.path },
                    preview: None,
                }
            })
            .collect();
        let text = match self.drafts.remove(key) {
            Some((text, mut kept)) => {
                attachments.append(&mut kept);
                super::selection::joined(saved.text, text)
            }
            None => saved.text,
        };
        self.drafts.insert(key.clone(), (text, attachments));
    }

    /// Saves what the box shows, as the draft of the session it shows.
    pub(super) fn save_shown(&mut self, cx: &mut Context<Self>) {
        let Some(key) = self.shown_key() else {
            return;
        };
        let draft = SavedDraft {
            text: self.draft.read(cx).value().to_string(),
            attachments: saved_images(&self.attachments),
        };
        self.save_draft(key, draft, cx);
    }

    /// Saves the draft put away under `key`.
    pub(super) fn save_kept(&mut self, key: &SessionKey, cx: &mut Context<Self>) {
        let draft = self.drafts.get(key).map(|(text, attachments)| SavedDraft {
            text: text.clone(),
            attachments: saved_images(attachments),
        });
        self.save_draft(key.clone(), draft.unwrap_or_default(), cx);
    }

    /// Resolves once every save sent has finished; `None` when none is going.
    pub fn saves_finished(&mut self) -> Option<oneshot::Receiver<()>> {
        if self.draft_saves.is_empty() {
            return None;
        }
        let (done, finished) = oneshot::channel();
        self.saves_done.push(done);
        Some(finished)
    }

    fn save_draft(&mut self, key: SessionKey, draft: SavedDraft, cx: &mut Context<Self>) {
        if !self.saved_drafts.contains_key(&key) {
            return;
        }
        match self.draft_saves.get_mut(&key) {
            Some(save) => save.next = Some(draft),
            None => self.send_save(key, draft, cx),
        }
    }

    fn send_save(&mut self, key: SessionKey, draft: SavedDraft, cx: &mut Context<Self>) {
        let args = json!({ "session": key.id, "text": draft.text, "attachments": draft.attachments,
            "expectedSource": self.saved_drafts.get(&key) });
        let call = self.client.call("drafts", "set", args, None, cx);
        let saved = key.clone();
        let call = cx.spawn(async move |composer, cx| {
            // One that fails is saved again by the session's next change.
            let _ = call.await;
            let _ = composer.update(cx, |composer, cx| composer.save_answered(saved, cx));
        });
        self.draft_saves.insert(
            key,
            DraftSave {
                next: None,
                _call: call,
            },
        );
    }

    fn save_answered(&mut self, key: SessionKey, cx: &mut Context<Self>) {
        match self.draft_saves.remove(&key).and_then(|save| save.next) {
            Some(next) => self.send_save(key, next, cx),
            None if self.draft_saves.is_empty() => {
                for done in self.saves_done.drain(..) {
                    let _ = done.send(());
                }
            }
            None => {}
        }
    }

    /// The session whose draft the box shows: the picked one while its open is on the way, once its owner is known.
    fn shown_key(&self) -> Option<SessionKey> {
        match &self.parked {
            Some(parked) => parked.key(),
            None => self.session_key.clone(),
        }
    }
}

fn saved_images(attachments: &[Attachment]) -> Vec<SavedImage> {
    (attachments.iter())
        .filter_map(|attachment| match &attachment.state {
            AttachmentState::Saved { path } => Some(SavedImage {
                name: attachment.name.to_string(),
                size: attachment.size,
                path: path.clone(),
            }),
            _ => None,
        })
        .collect()
}
