//! One bounded draft per owning checkout/path. Editor renderers borrow this model; it survives view teardown.

use std::collections::{HashMap, HashSet};
use std::path::{Component, Path};

#[cfg(test)]
#[path = "document_store_tests.rs"]
mod tests;

const MAX_TEXT: usize = 8 * 1024 * 1024;
const MAX_DIRTY: usize = 8;
const MAX_DRAFT_BYTES: usize = 64 * 1024 * 1024;

#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub struct DocumentKey {
    pub machine: String,
    pub checkout_root: String,
    pub path: String,
}

#[derive(Clone, Debug)]
pub struct TextEdit {
    pub start: usize,
    pub end: usize,
    pub replacement: String,
}

#[derive(Clone, Debug)]
pub struct DocumentSnapshot {
    pub key: DocumentKey,
    pub version: u64,
    pub base_revision: String,
    pub text: String,
}

#[derive(Clone, Debug)]
pub struct DraftEdit {
    pub key: DocumentKey,
    pub expected_version: u64,
    pub edits: Vec<TextEdit>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DraftFailure {
    Stale,
    OutsideRoot,
    InvalidRange,
    OverLimit,
    NotLoaded,
}

#[derive(Clone, Debug, Default)]
pub struct DocumentView {
    pub selection: std::ops::Range<usize>,
    pub scroll: (f32, f32),
}

struct Document {
    snapshot: DocumentSnapshot,
    saved_version: u64,
    view: DocumentView,
}

#[derive(Default)]
pub struct DocumentStore {
    documents: HashMap<DocumentKey, Document>,
}

impl gpui_kit::Global for DocumentStore {}

impl DocumentStore {
    pub fn open(
        &mut self,
        key: DocumentKey,
        revision: String,
        text: String,
    ) -> Result<(), DraftFailure> {
        if key.machine.is_empty()
            || key.checkout_root.is_empty()
            || key.path.is_empty()
            || key.path.len() > 4096
            || key.path.contains('\0')
            || Path::new(&key.path).is_absolute()
            || Path::new(&key.path)
                .components()
                .any(|p| !matches!(p, Component::Normal(_)))
        {
            return Err(DraftFailure::OutsideRoot);
        }
        if text.len() > MAX_TEXT {
            return Err(DraftFailure::OverLimit);
        }
        self.documents
            .entry(key.clone())
            .or_insert_with(|| Document {
                snapshot: DocumentSnapshot {
                    key,
                    version: 0,
                    base_revision: revision,
                    text,
                },
                saved_version: 0,
                view: DocumentView::default(),
            });
        Ok(())
    }

    pub fn stash_view(&mut self, key: &DocumentKey, view: DocumentView) {
        if let Some(document) = self.documents.get_mut(key) {
            document.view = view;
        }
    }

    pub fn view_state(&self, key: &DocumentKey) -> Option<DocumentView> {
        self.documents
            .get(key)
            .map(|document| document.view.clone())
    }

    pub fn release_clean(&mut self, key: &DocumentKey) -> bool {
        if self.is_dirty(key) {
            return false;
        }
        self.documents.remove(key).is_some()
    }

    pub fn snapshot(&self, key: &DocumentKey) -> Result<DocumentSnapshot, DraftFailure> {
        self.documents
            .get(key)
            .map(|d| d.snapshot.clone())
            .ok_or(DraftFailure::NotLoaded)
    }

    pub fn is_dirty(&self, key: &DocumentKey) -> bool {
        self.documents
            .get(key)
            .is_some_and(|d| d.snapshot.version != d.saved_version)
    }

    pub fn replace(
        &mut self,
        key: &DocumentKey,
        expected_version: u64,
        text: String,
    ) -> Result<u64, DraftFailure> {
        let current = self.documents.get(key).ok_or(DraftFailure::NotLoaded)?;
        if current.snapshot.version != expected_version {
            return Err(DraftFailure::Stale);
        }
        self.apply_batch(&[DraftEdit {
            key: key.clone(),
            expected_version,
            edits: vec![TextEdit {
                start: 0,
                end: current.snapshot.text.len(),
                replacement: text,
            }],
        }])?;
        Ok(self.documents[key].snapshot.version)
    }

    /// All ranges and all target versions are checked before any target changes.
    pub fn apply_batch(&mut self, batch: &[DraftEdit]) -> Result<(), DraftFailure> {
        let mut staged = HashMap::new();
        let mut seen = HashSet::new();
        for change in batch {
            if !seen.insert(change.key.clone()) {
                return Err(DraftFailure::InvalidRange);
            }
            let document = self
                .documents
                .get(&change.key)
                .ok_or(DraftFailure::NotLoaded)?;
            if document.snapshot.version != change.expected_version {
                return Err(DraftFailure::Stale);
            }
            let text = &document.snapshot.text;
            let mut edits: Vec<_> = change.edits.iter().collect();
            edits.sort_by_key(|e| (e.start, e.end));
            let mut last = 0;
            let mut previous = None;
            for edit in &edits {
                if edit.start < last
                    || edit.start > edit.end
                    || edit.end > text.len()
                    || !text.is_char_boundary(edit.start)
                    || !text.is_char_boundary(edit.end)
                    || previous == Some(edit.start)
                {
                    return Err(DraftFailure::InvalidRange);
                }
                previous = Some(edit.start);
                last = edit.end;
            }
            let removed: usize = edits.iter().map(|edit| edit.end - edit.start).sum();
            let added = edits
                .iter()
                .try_fold(0usize, |size, edit| {
                    size.checked_add(edit.replacement.len())
                })
                .ok_or(DraftFailure::OverLimit)?;
            if text.len() - removed > MAX_TEXT || added > MAX_TEXT - (text.len() - removed) {
                return Err(DraftFailure::OverLimit);
            }
            let mut replacement = text.clone();
            for edit in edits.into_iter().rev() {
                replacement.replace_range(edit.start..edit.end, &edit.replacement);
            }
            staged.insert(change.key.clone(), replacement);
        }
        let mut dirty = 0;
        let mut bytes = 0usize;
        for (key, document) in &self.documents {
            let replacement = staged.get(key);
            let changed = replacement.is_some_and(|text| *text != document.snapshot.text);
            if self.is_dirty(key) || changed {
                dirty += 1;
                bytes += replacement.unwrap_or(&document.snapshot.text).len();
            }
            if changed && document.snapshot.version == u64::MAX {
                return Err(DraftFailure::OverLimit);
            }
        }
        if dirty > MAX_DIRTY || bytes > MAX_DRAFT_BYTES {
            return Err(DraftFailure::OverLimit);
        }
        for (key, text) in staged {
            let document = self.documents.get_mut(&key).expect("validated target");
            if document.snapshot.text != text {
                document.snapshot.text = text;
                document.snapshot.version += 1;
            }
        }
        Ok(())
    }

    pub fn saved(&mut self, key: &DocumentKey, sent_version: u64, revision: String) {
        if let Some(document) = self.documents.get_mut(key)
            && sent_version >= document.saved_version
            && sent_version <= document.snapshot.version
        {
            document.saved_version = sent_version;
            document.snapshot.base_revision = revision;
        }
    }
}
