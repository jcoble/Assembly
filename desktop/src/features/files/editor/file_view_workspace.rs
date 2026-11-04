//! Canonical workspace state over the existing file views and document identities.
use super::{DocumentKey, DocumentStore, DocumentView, FileView};
use crate::core::state::workspace_panes::WorkspaceKey;
use crate::core::ui::center_panes::CenterPanes;
use gpui_kit::component::dock::PanelId;
use gpui_kit::*;
#[derive(Clone, Debug, PartialEq)]
pub struct LegacyFileCapture {
    pub legacy_session: String,
    pub path: String,
    pub owner: Option<WorkspaceKey>,
    pub document: Option<DocumentKey>,
    pub version: Option<u64>,
    pub entry: serde_json::Value,
    pub active: bool,
}
pub(crate) struct WorkspaceTab {
    pub path: String,
    pub active: bool,
    pub recent: u64,
    pub view: DocumentView,
}
#[derive(Default)]
struct Activity(u64);
impl Global for Activity {}
impl FileView {
    pub(crate) fn workspace_source(
        key: &WorkspaceKey,
        cx: &App,
    ) -> Option<crate::core::socket::row_source::ExpectedRowSource> {
        let selected = crate::core::state::selected_session::SelectedSession::try_global(cx)?;
        let selected = selected.read(cx);
        let identity = selected.key()?;
        if identity.machine != key.machine || identity.id != key.session_id {
            return None;
        }
        selected
            .source()
            .filter(|source| source.machine == key.machine)
            .cloned()
    }

    pub(crate) fn layout_ref(&self) -> (&str, &str) {
        (&self.session, &self.path)
    }
    pub(crate) fn restored_workspace_path(&self, key: &WorkspaceKey) -> Option<&str> {
        (self.owning_workspace().as_ref() == Some(key)
            || self.expected_workspace.as_ref() == Some(key))
        .then_some(self.path.as_str())
    }
    pub(crate) fn workspace_path(&self, key: &WorkspaceKey) -> Result<&str, String> {
        if !self.rename_ready() {
            return Err(
                "Finish the current file operation before capturing its center tab.".into(),
            );
        }
        // A saved tab keeps its validated checkout until it's shown, which is when its owner is first read.
        self.restored_workspace_path(key)
            .ok_or_else(|| "A file tab has no confirmed owning workspace identity.".to_string())
    }
    pub(crate) fn captured_workspace_paths(
        key: &WorkspaceKey,
        cx: &App,
    ) -> std::collections::HashSet<String> {
        super::super::file_codec::captured_paths(key, cx)
    }
    pub(super) fn activated_for_workspace(&mut self, cx: &mut App) {
        if !cx.has_global::<Activity>() {
            cx.set_global(Activity::default());
        }
        let activity = cx.global_mut::<Activity>();
        activity.0 = activity.0.saturating_add(1);
        self.last_active = activity.0;
    }
    pub(crate) fn workspace_tab(&self, key: &WorkspaceKey, cx: &App) -> Option<WorkspaceTab> {
        let matches = self.owning_workspace().as_ref() == Some(key)
            || self.document.as_ref().is_some_and(|document| {
                document.machine == key.machine && document.checkout_root == key.checkout_root
            })
            || (self.session == key.session_id
                && self.owner.as_ref().is_none_or(|owner| {
                    owner.machine == key.machine && owner.checkout_root == key.checkout_root
                }));
        if !matches {
            return None;
        }
        let view = self.current_document_view(cx);
        Some(WorkspaceTab {
            path: self.path.clone(),
            active: self.active,
            recent: self.last_active,
            view,
        })
    }
    fn current_document_view(&self, cx: &App) -> DocumentView {
        self.editor
            .as_ref()
            .map(|editor| {
                let state = editor.state.read(cx);
                let scroll = state.scroll_offset();
                DocumentView {
                    selection: state.selected_range(),
                    scroll: (f32::from(scroll.x), f32::from(scroll.y)),
                }
            })
            .or_else(|| {
                self.document
                    .as_ref()
                    .and_then(|key| cx.try_global::<DocumentStore>()?.view_state(key))
            })
            .or_else(|| self.restored_view.clone())
            .unwrap_or_default()
    }
    fn owning_workspace(&self) -> Option<WorkspaceKey> {
        self.owner.as_ref().map(|owner| WorkspaceKey {
            machine: owner.machine.clone(),
            session_id: owner.remote_session_id.clone(),
            checkout_root: owner.checkout_root.clone(),
        })
    }
    /// Rechecks versions and small metadata without re-cloning every draft during each migration RPC.
    pub fn legacy_matches(&self, captured: &LegacyFileCapture, cx: &App) -> bool {
        if !self.rename_ready()
            || self.session != captured.legacy_session
            || self.path != captured.path
            || self.owning_workspace() != captured.owner
            || self.document != captured.document
            || self.active != captured.active
        {
            return false;
        }
        let docs = cx.try_global::<DocumentStore>();
        let version = self.document.as_ref().and_then(|key| docs?.version(key));
        let base = self
            .document
            .as_ref()
            .and_then(|key| docs?.base_revision(key));
        let dirty = self
            .document
            .as_ref()
            .is_some_and(|key| docs.is_some_and(|docs| docs.is_dirty(key)));
        version == captured.version
            && base
                == captured
                    .entry
                    .get("baseRevision")
                    .and_then(serde_json::Value::as_str)
            && dirty == captured.entry.get("draft").is_some()
            && captured.entry["viewState"]
                == crate::features::files::file_codec::legacy_view(self.current_document_view(cx))
    }
    pub fn validate_legacy_snapshot(
        value: &serde_json::Value,
        key: &WorkspaceKey,
        cx: &App,
    ) -> Result<(), String> {
        let files = serde_json::json!({"files":value.get("files").cloned().unwrap_or_else(||serde_json::json!([])),"activePath":value.get("activePath").cloned().unwrap_or(serde_json::Value::Null)});
        crate::core::state::workspace_panes::PaneCodec::validate(
            &crate::features::files::file_codec::FilesCodec,
            &files,
            key,
            value,
            cx,
        )
    }
    pub fn legacy_capture(&self, cx: &App) -> Result<LegacyFileCapture, String> {
        if !self.rename_ready() {
            return Err("Finish the file operation before migrating its workspace.".into());
        }
        let owner = self.owning_workspace();
        let version = self
            .document
            .as_ref()
            .and_then(|document| cx.try_global::<DocumentStore>()?.version(document));
        Ok(LegacyFileCapture {
            legacy_session: self.session.clone(),
            path: self.path.clone(),
            owner,
            document: self.document.clone(),
            version,
            entry: crate::features::files::file_codec::legacy_entry(
                self.path.clone(),
                self.document.as_ref(),
                self.current_document_view(cx),
                cx,
            ),
            active: self.active,
        })
    }
    pub(crate) fn restore_workspace(
        key: &WorkspaceKey,
        path: String,
        view: Option<DocumentView>,
        window: &mut Window,
        cx: &mut App,
    ) {
        let alias = super::key(&key.session_id, &path);
        let workspace = key.clone();
        if let Some(id) = CenterPanes::open(alias, window, cx, |_, cx| {
            cx.new(|cx| {
                let mut file = Self::new(workspace.session_id.clone(), path.clone(), cx);
                file.source = Self::workspace_source(&workspace, cx);
                file.expected_workspace = Some(workspace.clone());
                file.restored_view = view;
                let document = DocumentKey {
                    machine: workspace.machine.clone(),
                    checkout_root: workspace.checkout_root.clone(),
                    path,
                };
                if cx
                    .try_global::<DocumentStore>()
                    .is_some_and(|docs| docs.version(&document).is_some())
                {
                    file.document = Some(document);
                    file.restored_view = None;
                }
                file
            })
        }) && let Some(file) = Self::panel(id, cx)
        {
            if let Some(document) = file.read(cx).document.clone() {
                let claim = format!(
                    "document:{}",
                    serde_json::to_string(&(
                        &document.machine,
                        &document.checkout_root,
                        &document.path
                    ))
                    .expect("document identity")
                );
                CenterPanes::claim_file(
                    claim,
                    super::key(&key.session_id, &document.path),
                    PanelId::from(file.entity_id()),
                    cx,
                );
            }
        }
    }
}
