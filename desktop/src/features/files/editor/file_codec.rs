//! Files metadata and genuine drafts in the one workspace registry, scoped to the navigator's owning checkout.
use super::document_store::{
    DocumentKey, DocumentStore, DocumentView, RestoredDraft, validate_key,
};
use super::file_view::FileView;
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
        let candidates = views
            .iter()
            .filter_map(|view| {
                let view = view.read(cx);
                let tab = view.workspace_tab(key, cx)?;
                let document = document_key(key, &tab.path);
                Some(Candidate {
                    active: tab.active,
                    recent: tab.recent,
                    entry: capture_entry(tab.path, Some(&document), tab.view, cx),
                })
            })
            .collect();
        let drafts = cx
            .try_global::<DocumentStore>()
            .map(|docs| {
                docs.dirty_keys(&key.machine, &key.checkout_root)
                    .into_iter()
                    .map(|document| Entry {
                        path: document.path.clone(),
                        base_revision: docs.base_revision(&document).map(str::to_owned),
                        draft: docs.draft(&document).map(str::to_owned),
                        view_state: docs.view_state(&document).map(view_state),
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
        for entry in snapshot.files {
            FileView::restore_workspace(
                &key,
                entry.path,
                entry.view_state.map(document_view),
                window,
                cx,
            );
        }
        if let Some(path) = snapshot.active_path {
            let source = FileView::workspace_source(&key, cx);
            FileView::open_at(key.session_id, path, None, None, true, source, window, cx);
        }
    }
}
impl Snapshot {
    fn drafts(&self, key: &WorkspaceKey) -> Vec<RestoredDraft> {
        self.files
            .iter()
            .filter_map(|entry| {
                Some(RestoredDraft {
                    key: document_key(key, &entry.path),
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
    let mut clean = 0;
    for entry in &snapshot.files {
        validate_key(&document_key(key, &entry.path))
            .map_err(|_| "A stored file path is outside its checkout.".to_string())?;
        if !seen.insert(&entry.path) {
            return Err("The stored file tabs contain a duplicate path.".into());
        }
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
            .is_some_and(|active| !seen.contains(active))
    {
        return Err("The saved file tab selection is invalid.".into());
    }
    Ok(snapshot)
}
fn select(candidates: Vec<Candidate>, drafts: Vec<Entry>) -> Snapshot {
    let active_path = candidates
        .iter()
        .find(|candidate| candidate.active)
        .map(|candidate| candidate.entry.path.clone());
    let keep = retained_clean(candidates.iter().map(|candidate| {
        (
            candidate.entry.path.as_str(),
            candidate.active,
            candidate.recent,
            candidate.entry.draft.is_some(),
        )
    }));
    let mut files: Vec<_> = candidates
        .iter()
        .filter(|candidate| {
            candidate.entry.draft.is_some() || keep.contains(candidate.entry.path.as_str())
        })
        .map(|candidate| candidate.entry.clone())
        .collect();
    for draft in drafts {
        if !files.iter().any(|entry| entry.path == draft.path) {
            files.push(draft);
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
fn document_key(key: &WorkspaceKey, path: &str) -> DocumentKey {
    DocumentKey {
        machine: key.machine.clone(),
        checkout_root: key.checkout_root.clone(),
        path: path.into(),
    }
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
/// The center order uses the same retention decision without cloning genuine draft payloads a second time.
pub(crate) fn captured_paths(key: &WorkspaceKey, cx: &App) -> HashSet<String> {
    let docs = cx.try_global::<DocumentStore>();
    let tabs: Vec<_> = CenterPanes::file_views(cx)
        .iter()
        .filter_map(|view| view.read(cx).workspace_tab(key, cx))
        .collect();
    let dirty = |path: &str| docs.is_some_and(|docs| docs.is_dirty(&document_key(key, path)));
    let mut paths: HashSet<String> = retained_clean(
        tabs.iter()
            .map(|tab| (tab.path.as_str(), tab.active, tab.recent, dirty(&tab.path))),
    )
    .into_iter()
    .map(str::to_owned)
    .collect();
    paths.extend(
        tabs.iter()
            .filter(|tab| dirty(&tab.path))
            .map(|tab| tab.path.clone()),
    );
    if let Some(docs) = docs {
        paths.extend(
            docs.dirty_keys(&key.machine, &key.checkout_root)
                .into_iter()
                .map(|document| document.path),
        );
    }
    paths
}
