//! Files metadata and genuine drafts in the one workspace registry, scoped to the navigator's owning checkout.
use super::document_store::{
    DocumentKey, DocumentStore, DocumentView, RestoredDraft, validate_key,
};
use super::file_view::{FileView, WorkspaceTab, outside_document, workspace_document};
use crate::core::intents::open_file::OutsideFile;
use crate::core::state::workspace_panes::{PaneCodec, WorkspaceKey, WorkspacePanes};
use crate::core::ui::center_panes::CenterPanes;
use gpui_kit::*;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::HashSet;

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ViewState {
    selection: [usize; 2],
    scroll: [f32; 2],
}
#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Entry {
    path: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    base_revision: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    draft: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    view_state: Option<ViewState>,
    /// Set for an outside file, whose `path` is then its real absolute path.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    outside: Option<SavedOutside>,
}
/// Where an outside file lives: its route machine (`None` for this one) and its owning engine's machine id, so its
/// draft key is known without asking the owner.
#[derive(Clone, Debug, PartialEq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(crate) struct SavedOutside {
    pub machine: Option<String>,
    pub engine: String,
}
#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Snapshot {
    files: Vec<Entry>,
    #[serde(default)]
    active_path: Option<String>,
}
struct Candidate {
    entry: Entry,
    identity: String,
    active: bool,
    recent: u64,
}
pub struct FilesCodec;
impl PaneCodec for FilesCodec {
    fn name(&self) -> &'static str {
        "files"
    }
    fn capture(&self, _: &Window, cx: &App) -> Result<Value, String> {
        let Some(key) = WorkspacePanes::key(cx) else {
            return Err("The owning workspace identity is unavailable.".into());
        };
        let views = CenterPanes::file_views(cx);
        // Refuse departure while a real file operation or its confirmation is still owned by a view.
        if views.iter().any(|view| !view.read(cx).rename_ready()) {
            return Err("Finish the current file operation before switching workspaces.".into());
        }
        let candidates = workspace_tabs(key, cx)
            .into_iter()
            .map(|tab| {
                let mut entry = capture_entry(tab.path, Some(&tab.document), tab.view, cx);
                entry.outside = tab.outside;
                Candidate {
                    active: tab.active,
                    recent: tab.recent,
                    identity: tab.document.identity(),
                    entry,
                }
            })
            .collect();
        let drafts = cx
            .try_global::<DocumentStore>()
            .map(|docs| {
                docs.dirty_keys(&key.machine, &key.checkout_root)
                    .into_iter()
                    .map(|document| {
                        let entry = Entry {
                            path: document.path.clone(),
                            base_revision: docs.base_revision(&document).map(str::to_owned),
                            draft: docs.draft(&document).map(str::to_owned),
                            view_state: docs.view_state(&document).map(view_state),
                            outside: None,
                        };
                        (entry, document.identity())
                    })
                    .collect()
            })
            .unwrap_or_default();
        serde_json::to_value(select(candidates, drafts))
            .map_err(|error| format!("The file workspace could not be captured: {error}"))
    }
    fn validate(
        &self,
        value: &Value,
        key: &WorkspaceKey,
        _: &Value,
        cx: &App,
    ) -> Result<(), String> {
        let snapshot = parse(value, key)?;
        let drafts = snapshot.drafts(key);
        if CenterPanes::file_views(cx)
            .iter()
            .any(|view| !view.read(cx).rename_ready())
        {
            return Err("Finish the current file operation before switching workspaces.".into());
        }
        if let Some(docs) = cx.try_global::<DocumentStore>() {
            docs.can_restore_drafts(&drafts)
        } else {
            DocumentStore::default().can_restore_drafts(&drafts)
        }
        .map_err(|error| {
            format!("The stored drafts cannot be restored: {error:?}. Current drafts remain open.")
        })
    }
    fn deactivate(&self, window: &mut Window, cx: &mut App) {
        let views = CenterPanes::file_views(cx);
        if let Some(dock) = CenterPanes::dock(cx) {
            for view in views {
                dock.update(cx, |dock, cx| dock.remove_panel(view, window, cx));
            }
        }
    }
    fn restore(&self, value: &Value, window: &mut Window, cx: &mut App) {
        let Some(key) = WorkspacePanes::key(cx).cloned() else {
            return;
        };
        let snapshot = match parse(value, &key) {
            Ok(snapshot) => snapshot,
            Err(error) => {
                WorkspacePanes::refuse_restore(error, cx);
                return;
            }
        };
        if !cx.has_global::<DocumentStore>() {
            cx.set_global(DocumentStore::default());
        }
        if let Err(error) = cx
            .global_mut::<DocumentStore>()
            .restore_drafts(snapshot.drafts(&key))
        {
            WorkspacePanes::refuse_restore(
                format!("The stored drafts could not be restored: {error:?}."),
                cx,
            );
            return;
        }
        self.deactivate(window, cx);
        let active = (snapshot.active_path.as_ref())
            .and_then(|path| snapshot.files.iter().find(|entry| &entry.path == path))
            .map(|entry| (entry.path.clone(), entry.outside.clone()));
        for entry in snapshot.files {
            let view = entry.view_state.map(document_view);
            match entry.outside {
                Some(outside) => {
                    let file = OutsideFile {
                        machine: outside.machine,
                        path: entry.path,
                    };
                    FileView::restore_outside(&key, file, outside.engine, view, window, cx)
                }
                None => FileView::restore_workspace(&key, entry.path, view, window, cx),
            }
        }
        match active {
            Some((path, Some(outside))) => {
                let file = OutsideFile {
                    machine: outside.machine,
                    path,
                };
                FileView::open_outside(file, (None, None), true, window, cx);
            }
            Some((path, None)) => {
                let source = FileView::workspace_source(&key, cx);
                FileView::open_at(key.session_id, path, (None, None), true, source, window, cx);
            }
            None => {}
        }
    }
}
impl Snapshot {
    fn drafts(&self, key: &WorkspaceKey) -> Vec<RestoredDraft> {
        self.files
            .iter()
            .filter_map(|entry| {
                Some(RestoredDraft {
                    key: entry_document(key, entry)?,
                    base_revision: entry.base_revision.clone()?,
                    text: entry.draft.clone()?,
                    view: entry
                        .view_state
                        .clone()
                        .map(document_view)
                        .unwrap_or_default(),
                })
            })
            .collect()
    }
}
fn parse(value: &Value, key: &WorkspaceKey) -> Result<Snapshot, String> {
    let snapshot: Snapshot = serde_json::from_value(value.clone())
        .map_err(|_| "Couldn't restore the saved file tabs.".to_string())?;
    let mut seen = HashSet::new();
    let mut paths = HashSet::new();
    let mut clean = 0;
    for entry in &snapshot.files {
        let document = entry_document(key, entry)
            .filter(|document| validate_key(document).is_ok())
            .ok_or("A stored file path is outside its checkout.")?;
        if !seen.insert(document.identity()) {
            return Err("The stored file tabs contain a duplicate path.".into());
        }
        paths.insert(entry.path.as_str());
        if entry.draft.is_some()
            && entry
                .base_revision
                .as_ref()
                .is_none_or(|revision| revision.is_empty())
        {
            return Err("A stored draft has no base revision.".into());
        }
        if entry.draft.is_none() {
            clean += 1;
        }
        if entry
            .view_state
            .as_ref()
            .is_some_and(|view| view.scroll.iter().any(|value| !value.is_finite()))
        {
            return Err("A stored file view is invalid.".into());
        }
    }
    if clean > 12
        || snapshot
            .active_path
            .as_ref()
            .is_some_and(|active| !paths.contains(active.as_str()))
    {
        return Err("The saved file tab selection is invalid.".into());
    }
    Ok(snapshot)
}
fn select(candidates: Vec<Candidate>, drafts: Vec<(Entry, String)>) -> Snapshot {
    let active_path = candidates
        .iter()
        .find(|candidate| candidate.active)
        .map(|candidate| candidate.entry.path.clone());
    let keep = retained_clean(candidates.iter().map(|candidate| {
        (
            candidate.identity.as_str(),
            candidate.active,
            candidate.recent,
            candidate.entry.draft.is_some(),
        )
    }));
    let kept: Vec<_> = candidates
        .iter()
        .filter(|candidate| {
            candidate.entry.draft.is_some() || keep.contains(candidate.identity.as_str())
        })
        .collect();
    let mut identities: HashSet<_> = kept.iter().map(|candidate| &candidate.identity).collect();
    let mut files: Vec<_> = kept
        .iter()
        .map(|candidate| candidate.entry.clone())
        .collect();
    for (draft, identity) in &drafts {
        if identities.insert(identity) {
            files.push(draft.clone());
        }
    }
    Snapshot { files, active_path }
}
fn capture_entry(
    path: String,
    document: Option<&DocumentKey>,
    view: DocumentView,
    cx: &App,
) -> Entry {
    let docs = cx.try_global::<DocumentStore>();
    Entry {
        path,
        base_revision: document
            .and_then(|key| docs?.base_revision(key))
            .map(str::to_owned),
        draft: document.and_then(|key| docs?.draft(key)).map(str::to_owned),
        view_state: Some(view_state(view)),
        outside: None,
    }
}
pub(super) fn legacy_view(view: DocumentView) -> Value {
    serde_json::to_value(view_state(view)).expect("finite file view metadata")
}
pub(super) fn legacy_entry(
    path: String,
    document: Option<&DocumentKey>,
    view: DocumentView,
    cx: &App,
) -> Value {
    serde_json::to_value(capture_entry(path, document, view, cx))
        .expect("finite file view metadata")
}
/// The draft key of a saved entry: under the workspace's checkout, or an outside file's engine and real parent.
fn entry_document(key: &WorkspaceKey, entry: &Entry) -> Option<DocumentKey> {
    match &entry.outside {
        Some(outside) => outside_document(&outside.engine, &entry.path),
        None => Some(workspace_document(key, &entry.path)),
    }
}
/// The document identities the saved Files snapshot lists, which its center tabs may name.
pub(crate) fn saved_identities(snapshot: &Value, key: &WorkspaceKey) -> HashSet<String> {
    (snapshot
        .get("files")
        .and_then(Value::as_array)
        .into_iter()
        .flatten())
    .filter_map(|file| serde_json::from_value::<Entry>(file.clone()).ok())
    .filter_map(|entry| entry_document(key, &entry))
    .map(|document| document.identity())
    .collect()
}
fn view_state(view: DocumentView) -> ViewState {
    ViewState {
        selection: [view.selection.start, view.selection.end],
        scroll: [view.scroll.0, view.scroll.1],
    }
}
fn document_view(view: ViewState) -> DocumentView {
    DocumentView {
        selection: view.selection[0]..view.selection[1],
        scroll: (view.scroll[0], view.scroll[1]),
    }
}
#[cfg(test)]
#[path = "file_codec_tests.rs"]
mod tests;

fn retained_clean<'a>(
    candidates: impl Iterator<Item = (&'a str, bool, u64, bool)>,
) -> HashSet<&'a str> {
    let mut clean: Vec<_> = candidates.filter(|(_, _, _, dirty)| !*dirty).collect();
    clean.sort_by_key(|(_, active, recent, _)| {
        (std::cmp::Reverse(*active), std::cmp::Reverse(*recent))
    });
    clean
        .into_iter()
        .take(12)
        .map(|(path, _, _, _)| path)
        .collect()
}
/// The workspace's file tabs, one per document: the first tab open on a document stands for it, as `claim_file`
/// keeps it, shown and recent when any tab on that document is. Two tabs can share one until the later is claimed,
/// such as an outside tab whose read failed on a session file's document.
fn workspace_tabs(key: &WorkspaceKey, cx: &App) -> Vec<WorkspaceTab> {
    let mut tabs: Vec<WorkspaceTab> = Vec::new();
    for tab in CenterPanes::file_views(cx)
        .iter()
        .filter_map(|view| view.read(cx).workspace_tab(key, cx))
    {
        match tabs.iter_mut().find(|kept| kept.document == tab.document) {
            Some(kept) => {
                kept.active |= tab.active;
                kept.recent = kept.recent.max(tab.recent);
            }
            None => tabs.push(tab),
        }
    }
    tabs
}
/// The center order uses the same retention decision without cloning genuine draft payloads a second time.
pub(crate) fn captured_identities(key: &WorkspaceKey, cx: &App) -> HashSet<String> {
    let docs = cx.try_global::<DocumentStore>();
    let tabs: Vec<_> = workspace_tabs(key, cx)
        .into_iter()
        .map(|tab| {
            let dirty = docs.is_some_and(|docs| docs.is_dirty(&tab.document));
            (tab.document.identity(), tab.active, tab.recent, dirty)
        })
        .collect();
    let mut identities: HashSet<String> =
        retained_clean(tabs.iter().map(|(identity, active, recent, dirty)| {
            (identity.as_str(), *active, *recent, *dirty)
        }))
        .into_iter()
        .map(str::to_owned)
        .collect();
    identities.extend(
        tabs.iter()
            .filter(|(_, _, _, dirty)| *dirty)
            .map(|(identity, _, _, _)| identity.clone()),
    );
    if let Some(docs) = docs {
        identities.extend(
            docs.dirty_keys(&key.machine, &key.checkout_root)
                .iter()
                .map(DocumentKey::identity),
        );
    }
    identities
}
