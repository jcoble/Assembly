//! An outside file: one opened by machine and real path rather than through a session. Its calls send a path target,
//! its draft is keyed by its owning engine, real parent folder and file name, and it opens locked, including when it
//! comes back with its workspace.
use super::{DocumentKey, FileView};
use crate::core::intents::open_file::OutsideFile;
use crate::core::state::workspace_panes::{WorkspaceKey, WorkspacePanes};
use crate::core::ui::center_panes::CenterPanes;
use crate::features::files::entry::FileOwnership;
use gpui_kit::*;
use serde_json::{Map, Value, json};

#[cfg(test)]
#[path = "file_view_outside_tests.rs"]
mod tests;

pub(crate) struct Outside {
    pub file: OutsideFile,
    /// The owning engine's machine id, from the owner's answer or the saved workspace.
    pub engine: Option<String>,
    /// The workspace that was active when it opened; it's saved and closed with that workspace.
    pub workspace: Option<WorkspaceKey>,
}

/// The tab key an outside file opens under, so the same file on the same machine opens once.
pub(crate) fn outside_key(machine: Option<&str>, path: &str) -> String {
    format!(
        "file:{}",
        serde_json::to_string(&("", machine, path)).expect("file alias")
    )
}

/// The draft identity of the outside file at the real absolute `path` on `engine`: its parent folder and file name.
pub(crate) fn outside_document(engine: &str, path: &str) -> Option<DocumentKey> {
    let (parent, name) = path.rsplit_once('/').filter(|_| path.starts_with('/'))?;
    Some(DocumentKey {
        machine: engine.into(),
        checkout_root: if parent.is_empty() { "/" } else { parent }.into(),
        path: name.into(),
    })
}

/// The tab key a saved File pane stands for, or `None` when its fields aren't exactly a session file's or an outside
/// file's.
pub(crate) fn saved_alias(fields: &Map<String, Value>) -> Option<String> {
    let text = |key: &str| fields.get(key).and_then(Value::as_str);
    let mut keys: Vec<_> = fields.keys().map(String::as_str).collect();
    keys.sort_unstable();
    match keys.as_slice() {
        ["path", "session"] => Some(super::key(text("session")?, text("path")?)),
        ["machine", "outside", "path"] if fields["outside"] == Value::Bool(true) => {
            let machine = &fields["machine"];
            if !machine.is_null() && !machine.is_string() {
                return None;
            }
            Some(outside_key(machine.as_str(), text("path")?))
        }
        _ => None,
    }
}

impl FileView {
    pub(crate) fn new_outside(
        file: OutsideFile,
        engine: Option<String>,
        workspace: Option<WorkspaceKey>,
        cx: &mut Context<Self>,
    ) -> Self {
        let mut view = Self::new(String::new(), file.path.clone(), cx);
        view.outside = Some(Outside {
            file,
            engine,
            workspace,
        });
        view.locked = true;
        view
    }

    /// Rebuilds a File pane from its saved fields and says the tab key it opens under. An outside file comes back
    /// locked.
    pub(super) fn from_saved(saved: &Value, cx: &mut App) -> (Entity<Self>, String) {
        let text = |key: &str| saved[key].as_str().unwrap_or_default().to_string();
        if saved["outside"] == Value::Bool(true) {
            let file = OutsideFile {
                machine: saved["machine"].as_str().map(str::to_owned),
                path: text("path"),
            };
            let alias = outside_key(file.machine.as_deref(), &file.path);
            (cx.new(|cx| Self::new_outside(file, None, None, cx)), alias)
        } else {
            let (session, path) = (text("session"), text("path"));
            let alias = super::key(&session, &path);
            (cx.new(|cx| Self::new(session, path, cx)), alias)
        }
    }

    /// What the dock saves for this pane.
    pub(super) fn saved_info(&self) -> Value {
        match &self.outside {
            Some(outside) => {
                json!({ "outside": true, "machine": outside.file.machine, "path": outside.file.path })
            }
            None => json!({ "session": self.session, "path": self.path }),
        }
    }

    /// The tab key a session's file opens under.
    pub(crate) fn session_tab_key(session: &str, path: &str) -> String {
        super::key(session, path)
    }

    #[cfg(test)]
    pub(crate) fn is_locked(&self) -> bool {
        self.locked
    }

    /// Puts the file's read text in its editor, as drawing its tab does; says whether it's there.
    #[cfg(test)]
    pub(crate) fn bind_for_test(&mut self, window: &mut Window, cx: &mut Context<Self>) -> bool {
        if self.text.content().is_some() && !self.text.is_loading() {
            self.bind_editor(window, cx);
        }
        self.document.is_some()
    }

    pub(crate) fn is_outside(&self) -> bool {
        self.outside.is_some()
    }

    pub(super) fn tab_key(&self) -> String {
        match &self.outside {
            Some(outside) => outside_key(outside.file.machine.as_deref(), &outside.file.path),
            None => super::key(&self.session, &self.path),
        }
    }

    /// The owner this file was read as, or for a restored outside file, the one it was saved as.
    fn expected_owner(&self) -> Option<FileOwnership> {
        self.owner.clone().or_else(|| {
            let outside = self.outside.as_ref()?;
            let document = outside_document(outside.engine.as_deref()?, &outside.file.path)?;
            Some(FileOwnership {
                machine: document.machine,
                checkout_root: document.checkout_root,
                remote_session_id: String::new(),
                file: Some(document.path),
            })
        })
    }

    /// The fields that name this file in a files call: its session and relative path, or its path target.
    pub(super) fn file_args(&self, mut extra: Value) -> Value {
        let fields = extra.as_object_mut().expect("call fields");
        match &self.outside {
            Some(outside) => {
                fields.insert(
                    "target".into(),
                    json!({ "path": outside.file.path, "machine": outside.file.machine }),
                );
                fields.insert("expectedOwner".into(), json!(self.expected_owner()));
            }
            None => {
                fields.insert("session".into(), json!(self.session));
                fields.insert("path".into(), json!(self.path));
                fields.insert("expectedSource".into(), json!(self.source));
                fields.insert("expectedOwner".into(), json!(self.owner));
            }
        }
        extra
    }

    /// The fields of this file's owner question.
    pub(super) fn owner_args(&self) -> Value {
        match &self.outside {
            Some(_) => self.file_args(json!({})),
            None => {
                json!({ "session": self.session, "expectedSource": self.source, "expectedOwner": self.owner })
            }
        }
    }

    /// The path this file's draft is keyed by under its owner: the session-relative path, or the outside file's name.
    pub(super) fn document_path(&self, owner: &FileOwnership) -> String {
        match &self.outside {
            Some(_) => owner.file.clone().unwrap_or_default(),
            None => self.path.clone(),
        }
    }

    /// The draft key this outside file is saved under in `key`'s workspace, when it belongs there.
    pub(crate) fn outside_document_in(&self, key: &WorkspaceKey) -> Option<DocumentKey> {
        let outside = self.outside.as_ref()?;
        if outside.workspace.as_ref() != Some(key) {
            return None;
        }
        outside_document(outside.engine.as_deref()?, &outside.file.path)
    }

    /// Opens `file` by its machine and real path, or brings its tab forward. It belongs to the active workspace.
    pub(crate) fn open_outside(
        file: OutsideFile,
        jump: (Option<u32>, Option<u32>),
        pinned: bool,
        window: &mut Window,
        cx: &mut App,
    ) {
        crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed(cx);
        let tab = outside_key(file.machine.as_deref(), &file.path);
        let workspace = WorkspacePanes::key(cx).cloned();
        let id = CenterPanes::open(tab, window, cx, |_, cx| {
            cx.new(|cx| {
                let mut view = Self::new_outside(file, None, workspace, cx);
                view.pinned = pinned;
                view
            })
        });
        if let Some(id) = id
            && let Some(view) = Self::panel(id, cx)
        {
            view.update(cx, |view, cx| view.arrive(pinned, jump, cx));
        }
    }

    /// Reopens a saved outside file with its workspace, locked, keyed by the engine it was saved under.
    pub(crate) fn restore_outside(
        key: &WorkspaceKey,
        file: OutsideFile,
        engine: String,
        view: Option<super::DocumentView>,
        window: &mut Window,
        cx: &mut App,
    ) {
        let Some(document) = outside_document(&engine, &file.path) else {
            return;
        };
        let alias = outside_key(file.machine.as_deref(), &file.path);
        let workspace = key.clone();
        let restored = cx
            .try_global::<super::DocumentStore>()
            .is_some_and(|docs| docs.version(&document).is_some());
        let id = CenterPanes::open(alias.clone(), window, cx, |_, cx| {
            cx.new(|cx| {
                let mut tab = Self::new_outside(file, Some(engine), Some(workspace.clone()), cx);
                tab.expected_workspace = Some(workspace);
                tab.restored_view = view;
                if restored {
                    tab.document = Some(document.clone());
                    tab.restored_view = None;
                }
                tab
            })
        });
        if let Some(id) = id
            && restored
        {
            CenterPanes::claim_file(document.identity(), alias, id, cx);
        }
    }
}
