//! One save attempt captures its exact document/version/bytes until confirmed or reconciled.
use super::document_store::{DocumentKey, DocumentSnapshot, DocumentStore};

#[cfg(test)]
#[path = "file_save_tests.rs"]
mod tests;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SaveState {
    Saving,
    Saved,
    Conflict,
    Failed,
    Unknown,
}

pub struct ConfirmedFileSave {
    pub key: DocumentKey,
    pub save_id: String,
    pub saved_version: u64,
    pub revision: String,
}

pub struct FileSaveController {
    pub captured: DocumentSnapshot,
    pub save_id: String,
    pub state: SaveState,
    completed: bool,
}

impl FileSaveController {
    pub fn begin(captured: DocumentSnapshot) -> Self {
        Self {
            captured,
            save_id: uuid::Uuid::new_v4().to_string(),
            state: SaveState::Saving,
            completed: false,
        }
    }

    pub fn unknown(&mut self) {
        if !self.completed {
            self.state = SaveState::Unknown;
        }
    }

    pub fn finish_confirmed(
        &mut self,
        revision: String,
        docs: &mut DocumentStore,
    ) -> Option<ConfirmedFileSave> {
        if self.completed {
            return None;
        }
        docs.saved(&self.captured.key, self.captured.version, revision.clone());
        self.completed = true;
        self.state = SaveState::Saved;
        Some(ConfirmedFileSave {
            key: self.captured.key.clone(),
            save_id: self.save_id.clone(),
            saved_version: self.captured.version,
            revision,
        })
    }

    pub fn reconcile(
        &mut self,
        disk_text: &str,
        revision: String,
        docs: &mut DocumentStore,
    ) -> Option<ConfirmedFileSave> {
        if self.state != SaveState::Unknown || disk_text != self.captured.text {
            return None;
        }
        self.finish_confirmed(revision, docs)
    }
}
