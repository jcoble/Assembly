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

impl DocumentKey {
    /// The name a file's tab is claimed and ordered under: one document, one tab.
    pub fn identity(&self) -> String {
        format!(
            "document:{}",
            serde_json::to_string(&(&self.machine, &self.checkout_root, &self.path))
                .expect("document identity")
        )
    }
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

pub struct RestoredDraft {
    pub key: DocumentKey,
    pub base_revision: String,
    pub text: String,
    pub view: DocumentView,
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
        validate_key(&key)?;
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

    /// Called only after explicit draft-loss confirmation for this exact version.
    pub fn discard(
        &mut self,
        key: &DocumentKey,
        expected_version: u64,
    ) -> Result<(), DraftFailure> {
        let document = self.documents.get(key).ok_or(DraftFailure::NotLoaded)?;
        if document.snapshot.version != expected_version {
            return Err(DraftFailure::Stale);
        }
        self.documents.remove(key);
        Ok(())
    }

    pub fn base_revision(&self, key: &DocumentKey) -> Option<&str> {
        self.documents
            .get(key)
            .map(|document| document.snapshot.base_revision.as_str())
    }
    pub fn draft(&self, key: &DocumentKey) -> Option<&str> {
        self.documents
            .get(key)
            .filter(|document| document.snapshot.version != document.saved_version)
            .map(|document| document.snapshot.text.as_str())
    }

    /// Pure preflight is shared by hydration validation and the actual atomic insertion.
    pub fn can_restore_drafts(&self, drafts: &[RestoredDraft]) -> Result<(), DraftFailure> {
        let mut seen = HashSet::new();
        let mut added = 0;
        let mut count = 0;
        for draft in drafts {
            validate_key(&draft.key)?;
            if draft.text.len() > MAX_TEXT {
                return Err(DraftFailure::OverLimit);
            }
            if !seen.insert(&draft.key) {
                return Err(DraftFailure::InvalidRange);
            }
            if self.documents.contains_key(&draft.key) {
                continue;
            }
            added += draft.text.len();
            count += 1;
        }
        let existing: Vec<_> = self
            .documents
            .values()
            .filter(|document| document.snapshot.version != document.saved_version)
            .collect();
        let bytes: usize = existing
            .iter()
            .map(|document| document.snapshot.text.len())
            .sum();
        if existing.len() + count > MAX_DIRTY || bytes + added > MAX_DRAFT_BYTES {
            return Err(DraftFailure::OverLimit);
        }
        Ok(())
    }

    /// A persisted draft is genuine unsaved text plus its base revision, without inventing original disk bytes.
    pub fn restore_drafts(&mut self, drafts: Vec<RestoredDraft>) -> Result<(), DraftFailure> {
        self.can_restore_drafts(&drafts)?;
        for draft in drafts {
            self.documents
                .entry(draft.key.clone())
                .or_insert_with(|| Document {
                    snapshot: DocumentSnapshot {
                        key: draft.key,
                        version: 1,
                        base_revision: draft.base_revision,
                        text: draft.text,
                    },
                    saved_version: 0,
                    view: draft.view,
                });
        }
        Ok(())
    }

    pub fn version(&self, key: &DocumentKey) -> Option<u64> {
        self.documents
            .get(key)
            .map(|document| document.snapshot.version)
    }

    pub fn dirty_keys(&self, machine: &str, root: &str) -> Vec<DocumentKey> {
        let mut keys: Vec<_> = self
            .documents
            .keys()
            .filter(|key| key.machine == machine && key.checkout_root == root && self.is_dirty(key))
            .cloned()
            .collect();
        keys.sort_by(|a, b| a.path.cmp(&b.path));
        keys
    }

    pub fn versions_under(&self, machine: &str, root: &str, path: &str) -> Vec<(DocumentKey, u64)> {
        let prefix = path.to_owned() + "/";
        let mut versions: Vec<_> = self
            .documents
            .iter()
            .filter(|(key, _)| {
                key.machine == machine
                    && key.checkout_root == root
                    && (key.path == path || key.path.starts_with(&prefix))
            })
            .map(|(key, document)| (key.clone(), document.snapshot.version))
            .collect();
        versions.sort_by(|(a, _), (b, _)| a.path.cmp(&b.path));
        versions
    }

    pub fn can_rename_paths(
        &self,
        machine: &str,
        root: &str,
        source: &str,
        destination: &str,
    ) -> Result<(), DraftFailure> {
        self.rename_targets(machine, root, source, destination)
            .map(|_| ())
    }

    /// The caller invokes this only after the owning backend confirms the path mutation.
    pub fn rename_paths(
        &mut self,
        machine: &str,
        root: &str,
        source: &str,
        destination: &str,
    ) -> Result<(), DraftFailure> {
        let targets = self.rename_targets(machine, root, source, destination)?;
        let mut moved = Vec::new();
        for (old, new) in targets {
            let mut document = self
                .documents
                .remove(&old)
                .expect("validated rename source");
            document.snapshot.key = new.clone();
            moved.push((new, document));
        }
        self.documents.extend(moved);
        Ok(())
    }

    fn rename_targets(
        &self,
        machine: &str,
        root: &str,
        source: &str,
        destination: &str,
    ) -> Result<Vec<(DocumentKey, DocumentKey)>, DraftFailure> {
        let prefix = source.to_owned() + "/";
        let mut targets = Vec::new();
        for key in self.documents.keys().filter(|key| {
            key.machine == machine
                && key.checkout_root == root
                && (key.path == source || key.path.starts_with(&prefix))
        }) {
            let path = format!("{}{}", destination, &key.path[source.len()..]);
            let new = DocumentKey {
                path,
                ..key.clone()
            };
            validate_key(&new)?;
            if &new != key && self.documents.contains_key(&new) {
                return Err(DraftFailure::Stale);
            }
            targets.push((key.clone(), new));
        }
        Ok(targets)
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

    pub fn reload(
        &mut self,
        key: &DocumentKey,
        expected_version: u64,
        revision: String,
        text: String,
    ) -> Result<(), DraftFailure> {
        let document = self.documents.get_mut(key).ok_or(DraftFailure::NotLoaded)?;
        if document.snapshot.version != expected_version {
            return Err(DraftFailure::Stale);
        }
        if text.len() > MAX_TEXT || document.snapshot.version == u64::MAX {
            return Err(DraftFailure::OverLimit);
        }
        document.snapshot.version += 1;
        document.saved_version = document.snapshot.version;
        document.snapshot.text = text;
        document.snapshot.base_revision = revision;
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

pub(super) fn validate_key(key: &DocumentKey) -> Result<(), DraftFailure> {
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
    Ok(())
}
