//! One source editor per opened file, backed by the bounded shared draft store and owning-host bulk file calls.

use gpui_kit::component::dock::{PanelId, PanelInfo, panel_handle, register_panel};
use gpui_kit::*;
use serde_json::{Value, json};

use super::document_store::{DocumentKey, DocumentSnapshot, DocumentStore, DocumentView};
use super::editor_surface::EditorSurface;
use super::entry::{FileChunk, FileOwnership};
use super::file_history::FileHistory;
use super::file_save::{ConfirmedFileSave, FileSaveController, SaveState};

#[path = "file_view_close.rs"]
mod closing;
#[path = "file_view_conflict.rs"]
mod conflict;
#[path = "file_view_editor.rs"]
mod editor;
#[path = "file_view_lock.rs"]
mod lock;
#[path = "file_view_outside.rs"]
mod outside;
#[path = "file_view_panel.rs"]
mod panel;
#[path = "file_view_save.rs"]
mod saving;
#[path = "file_view_workspace.rs"]
mod workspace;
use super::file_text::FileText;
use crate::core::engine::SessionKey;
use crate::core::intents::open_file::{OpenFile, names_its_own_place};
use crate::core::navigation::{file_link, file_link_whole};
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::state::preferences::Preferences;
use crate::core::ui::center_panes::CenterPanes;
pub(crate) use outside::{outside_document, saved_alias};
pub use workspace::LegacyFileCapture;
pub(crate) use workspace::{WorkspaceTab, workspace_document};

#[cfg(test)]
#[path = "file_view_tests.rs"]
mod tests;

/// The pane's name in the saved layout.
pub const FILE: &str = "File";
actions!(files, [SaveDocument, CloseDocument]);

pub struct FileView {
    focus: FocusHandle,
    pub(super) session: String,
    pub(super) path: String,
    /// Set for a file opened by machine and real path rather than through a session.
    outside: Option<outside::Outside>,
    /// Edits and saves wait for the person's say-so; outside files open locked.
    locked: bool,
    pub(super) text: FileText,
    /// The part being read; dropping it cancels its call.
    pub(super) loading: Option<Task<()>>,
    started: bool,
    owner: Option<FileOwnership>,
    source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    document: Option<DocumentKey>,
    editor: Option<EditorSurface>,
    editor_events: Option<Subscription>,
    editor_error: Option<String>,
    active: bool,
    jump: Option<(u32, u32)>,
    pinned: bool,
    last_active: u64,
    expected_workspace: Option<crate::core::state::workspace_panes::WorkspaceKey>,
    restored_view: Option<DocumentView>,
    save: Option<FileSaveController>,
    saving: Option<Task<()>>,
    upload: Option<String>,
    last_save: Option<ConfirmedFileSave>,
    history: Option<Entity<FileHistory>>,
    comparison: Option<Entity<super::file_comparison::FileComparison>>,
    history_shown: bool,
    prompting: Option<Task<()>>,
    reload_version: Option<u64>,
    closing_window: Option<AnyWindowHandle>,
    guard_saved: Option<futures_channel::oneshot::Sender<bool>>,
    /// Redraws in the editor's face, size and line height when Settings changes them.
    _preferences: Option<Subscription>,
}

impl FileView {
    /// Restores a file tab from the saved layout.
    pub fn register(cx: &mut App) {
        crate::core::state::workspace_panes::WorkspacePanes::register(
            Box::new(super::file_codec::FilesCodec),
            cx,
        )
        .expect("one files codec registration");
        let save = KeyBinding::load(
            "secondary-s",
            Box::new(SaveDocument),
            Some(
                KeyBindingContextPredicate::parse("FileEditor")
                    .expect("file editor key context")
                    .into(),
            ),
            false,
            None,
            cx,
        );
        let close = KeyBinding::load(
            "secondary-w",
            Box::new(CloseDocument),
            Some(
                KeyBindingContextPredicate::parse("FileEditor")
                    .expect("file editor key context")
                    .into(),
            ),
            false,
            None,
            cx,
        );
        cx.bind_keys([
            save.expect("the save-file binding parses"),
            close.expect("the close-file binding parses"),
        ]);
        register_panel(cx, FILE, |build, _, cx| {
            let saved = match build.state().info.clone() {
                PanelInfo::Panel(saved) => saved,
                _ => Value::Null,
            };
            let (view, alias) = FileView::from_saved(&saved, cx);
            CenterPanes::remember(alias, PanelId::from(view.entity_id()), cx);
            panel_handle(view)
        });
        // Global, so a link is heard wherever focus is and with no window active. It's the intent's only listener.
        cx.on_action(|intent: &OpenFile, cx| {
            let intent = intent.clone();
            // A link whose line suffix was read opens by its whole name when that file exists, which is asked first.
            if intent.whole.is_some() {
                return file_link_whole::open_whole_first(intent, cx);
            }
            // An absolute or `~` path goes to the one resolver, which opens it as a session file or an outside one. A
            // session that came with no source keeps its session, and the resolver finds its machine.
            if intent.outside.is_none() && names_its_own_place(&intent.path) {
                let jump = (intent.line, intent.column);
                let Some(source) = intent.expected_source else {
                    if intent.session_id.is_empty() {
                        return file_link::open(intent.path, None, None, jump, cx);
                    }
                    return file_link::open_unsourced(intent.path, intent.session_id, jump, cx);
                };
                let machine = source.route_machine.clone();
                let from = (!intent.session_id.is_empty()).then(|| {
                    let key = SessionKey {
                        machine: source.machine.clone(),
                        id: intent.session_id.clone(),
                    };
                    (key, source)
                });
                return file_link::open(intent.path, machine, from, jump, cx);
            }
            let Some(handle) = cx
                .active_window()
                .or_else(|| cx.windows().into_iter().next())
            else {
                return;
            };
            cx.defer(move |cx| {
                let _ = handle.update(cx, |_, window, cx| {
                    if let Some(file) = intent.outside {
                        let jump = (intent.line, intent.column);
                        return Self::open_outside(file, jump, intent.pinned, window, cx);
                    }
                    Self::open_at(
                        intent.session_id,
                        intent.path,
                        (intent.line, intent.column),
                        intent.pinned,
                        intent.expected_source,
                        window,
                        cx,
                    )
                });
            });
        });
    }

    pub fn new(session: String, path: String, cx: &mut Context<Self>) -> Self {
        FileView {
            focus: cx.focus_handle(),
            session,
            path,
            outside: None,
            locked: false,
            text: FileText::default(),
            loading: None,
            started: false,
            owner: None,
            source: None,
            document: None,
            editor: None,
            editor_events: None,
            editor_error: None,
            active: false,
            jump: None,
            pinned: true,
            last_active: 0,
            expected_workspace: None,
            restored_view: None,
            save: None,
            saving: None,
            upload: None,
            last_save: None,
            history: None,
            comparison: None,
            history_shown: false,
            prompting: None,
            reload_version: None,
            closing_window: None,
            guard_saved: None,
            _preferences: Preferences::try_global(cx)
                .map(|preferences| cx.observe(&preferences, |_, _, cx| cx.notify())),
        }
    }

    pub(super) fn load_part(&mut self, offset: u64, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_bulk(cx) else {
            self.text.failed("The backend isn't running.".into());
            return;
        };
        let args = self.file_args(json!({ "offset": offset, "revision": self.text.revision() }));
        let call = socket
            .read(cx)
            .call::<FileChunk>("files", "read", args, None);
        self.loading = Some(cx.spawn(async move |view, cx| {
            let outcome = call.await;
            let _ = view.update(cx, |view, cx| {
                view.loading = None;
                let answer = match outcome {
                    Outcome::Ok(chunk) => Ok(chunk),
                    Outcome::Failed(failure) => Err(failure.message),
                    Outcome::Aborted => {
                        Err("The connection dropped; open the file again to retry.".into())
                    }
                };
                if !view.active {
                    return;
                }
                if let Some(again) = view.text.loaded(offset, answer) {
                    view.load_part(again, cx);
                } else if let Some(next) = view.text.more(usize::MAX - 200) {
                    view.load_part(next, cx);
                }
                cx.notify();
            });
        }));
    }

    fn load_owner(&mut self, cx: &mut Context<Self>) {
        // An outside file needs no session's checkout; its own expected owner stands in for the workspace checks.
        if self.outside.is_none()
            && crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::try_global(
                cx,
            )
            .is_some()
            && crate::core::state::workspace_panes::WorkspacePanes::key(cx).is_none()
        {
            self.text
                .failed("This transcript has no recorded checkout to open files from.".into());
            return;
        }
        let Some(socket) = SocketClient::try_global(cx) else {
            self.text.failed("The backend isn't running.".into());
            return;
        };
        let call = socket
            .read(cx)
            .call::<FileOwnership>("files", "owner", self.owner_args(), None);
        self.loading = Some(cx.spawn(async move |view, cx| {
            let answer = call.await;
            let _ = view.update(cx, |view, cx| {
                view.loading = None;
                if !view.active {
                    return;
                }
                match answer {
                    Outcome::Ok(owner) => {
                        if view.outside.is_none() && view.expected_workspace.as_ref().is_some_and(|key| {
                            key.session_id != view.session
                                || key.machine != owner.machine
                                || key.checkout_root != owner.checkout_root
                        }) {
                            view.text.failed(
                                "The owning checkout changed. The saved draft is preserved.".into(),
                            );
                            cx.notify();
                            return;
                        }
                        if view.reload_version.is_some()
                            && view.document.as_ref().is_some_and(|key| {
                                key.machine != owner.machine
                                    || key.checkout_root != owner.checkout_root
                            })
                        {
                            view.text.failed(
                                "The owning checkout changed. The current draft is preserved."
                                    .into(),
                            );
                            cx.notify();
                            return;
                        }
                        if let Some(outside) = &mut view.outside {
                            outside.engine = Some(owner.machine.clone());
                        }
                        view.owner = Some(owner);
                        // A person's open was refused a save while its owner was unknown, so it's retried now.
                        if view.expected_workspace.is_none() {
                            crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed(cx);
                        }
                        if view.document.is_some() && view.reload_version.is_none() {
                            cx.notify();
                            return;
                        }
                        view.text.start();
                        view.load_part(0, cx);
                    }
                    Outcome::Failed(error) => view.text.failed(error.message),
                    Outcome::Aborted => view
                        .text
                        .failed("The connection dropped; reopen this file to retry.".into()),
                }
                cx.notify();
            });
        }));
    }

    pub fn open_at(
        session: String,
        path: String,
        (line, column): (Option<u32>, Option<u32>),
        pinned: bool,
        source: Option<crate::core::socket::row_source::ExpectedRowSource>,
        window: &mut Window,
        cx: &mut App,
    ) {
        // A restore runs inside the coordinator's own update, for the checkout it has already validated; its current
        // target is still the session being left, so only a person's open asks it.
        if !crate::core::state::workspace_panes::WorkspacePanes::is_restoring(cx)
            && let Some(coordinator) =
                crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::try_global(
                    cx,
                )
            && coordinator.read(cx).current_target().is_some_and(|target| {
                matches!(
                target,
                crate::features::workspaces::workspace_coordinator::WorkspaceTarget::TranscriptOnly(
                    _
                )
            )
            })
        {
            coordinator.update(cx, |view, cx| {
                view.status =
                    Some("This transcript has no recorded checkout to open files from.".into());
                cx.notify();
            });
            return;
        }
        crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed(cx);
        let tab_key = key(&session, &path);
        let id = CenterPanes::open(tab_key, window, cx, |_, cx| {
            cx.new(|cx| {
                let mut view = FileView::new(session, path, cx);
                view.pinned = pinned;
                view.source = source.clone();
                view
            })
        });
        if let Some(id) = id
            && let Some(view) = Self::panel(id, cx)
        {
            view.update(cx, |view, cx| {
                if view.source != source {
                    view.editor_error = Some("The file was opened from a different source. Its existing draft remains unchanged.".into());
                    cx.notify();return;
                }
                view.arrive(pinned, (line, column), cx);
            });
        }
    }

    pub fn open_history(
        session: String,
        path: String,
        source: Option<crate::core::socket::row_source::ExpectedRowSource>,
        window: &mut Window,
        cx: &mut App,
    ) {
        let alias = key(&session, &path);
        if let Some(id) = CenterPanes::open(alias, window, cx, |_, cx| {
            cx.new(|cx| {
                let mut view = Self::new(session, path, cx);
                view.source = source.clone();
                view
            })
        }) && let Some(view) = Self::panel(id, cx)
        {
            view.update(cx, |view, cx| {
                view.pinned = true;
                if !view.history_shown {
                    view.toggle_history(cx);
                }
            });
        }
    }

    fn toggle_history(&mut self, cx: &mut Context<Self>) {
        if !self.has_history() {
            return;
        }
        self.history_shown = !self.history_shown;
        if self.history.is_none() {
            self.history = Some(cx.new(|_| {
                FileHistory::new(self.session.clone(), self.path.clone(), self.source.clone())
            }));
        }
        if let Some(history) = &self.history {
            history.update(cx, |history, cx| {
                history.set_active(self.active && self.history_shown, cx)
            });
        }
        cx.notify();
    }

    pub(crate) fn matches_path(&self, machine: &str, root: &str, path: &str) -> bool {
        self.outside.is_none()
            && self.document.as_ref().is_some_and(|key| {
                key.machine == machine
                    && key.checkout_root == root
                    && (key.path == path || key.path.starts_with(&(path.to_string() + "/")))
            })
    }

    pub(crate) fn rename_ready(&self) -> bool {
        self.saving.is_none() && self.prompting.is_none()
    }

    pub(crate) fn renamed_keys(&self, source: &str, destination: &str) -> (String, String) {
        let key = self.document.as_ref().expect("matching document");
        let path = format!("{}{}", destination, &self.path[source.len()..]);
        let renamed = DocumentKey {
            path: path.clone(),
            ..key.clone()
        };
        (self::key(&self.session, &path), renamed.identity())
    }

    pub(crate) fn renamed(
        &mut self,
        source: &str,
        destination: &str,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> (String, String) {
        self.loading = None;
        self.release_read(cx);
        self.comparison = None;
        if let Some(history) = self.history.take() {
            history.update(cx, |history, cx| history.set_active(false, cx));
        }
        self.history_shown = false;
        self.path = format!("{}{}", destination, &self.path[source.len()..]);
        let key = self.document.as_mut().expect("matching document");
        key.path = self.path.clone();
        let real = key.identity();
        let snapshot = cx
            .global::<DocumentStore>()
            .snapshot(key)
            .expect("renamed document");
        self.editor_events = None;
        self.editor = None;
        if self.active {
            self.install_editor(snapshot, window, cx);
        }
        cx.notify();
        (self::key(&self.session, &self.path), real)
    }

    fn release_read(&mut self, cx: &mut Context<Self>) {
        if let (Some(socket), Some(revision)) = (SocketClient::try_bulk(cx), self.text.revision()) {
            // A target's release names the owner it was read as; a session's, its relative path.
            let args = match &self.outside {
                Some(_) => self.file_args(json!({ "revision": revision })),
                None => json!({ "session": self.session, "path": self.path, "revision": revision }),
            };
            socket
                .read(cx)
                .call::<Value>("files", "release", args, None)
                .ignore_reply();
        }
    }

    pub fn document_key(&self) -> Option<&DocumentKey> {
        self.document.as_ref()
    }

    /// The workspace guard consumes this existing save completion, never another save transport.
    pub fn save_for_guard(
        &mut self,
        answer: futures_channel::oneshot::Sender<bool>,
        cx: &mut Context<Self>,
    ) {
        if self.saving.is_some() || self.guard_saved.is_some() || self.document.is_none() {
            let _ = answer.send(false);
            return;
        }
        self.guard_saved = Some(answer);
        self.save_document(cx);
        if self.saving.is_none() {
            self.finish_guard_save(false);
        }
    }

    pub(super) fn finish_guard_save(&mut self, confirmed: bool) {
        if let Some(answer) = self.guard_saved.take() {
            let _ = answer.send(confirmed);
        }
    }

    pub fn discard_for_guard(&mut self, expected: u64, cx: &mut Context<Self>) -> bool {
        if self.saving.is_some() {
            return false;
        }
        let Some(key) = &self.document else {
            return false;
        };
        if cx
            .global_mut::<DocumentStore>()
            .discard(key, expected)
            .is_err()
        {
            return false;
        }
        self.document = None;
        self.editor_events = None;
        self.editor = None;
        self.save = None;
        self.comparison = None;
        self.text.start();
        if self.active {
            self.load_owner(cx);
        } else {
            self.started = false;
        }
        cx.notify();
        true
    }

    pub(super) fn document_snapshot(&self, cx: &App) -> Option<DocumentSnapshot> {
        cx.try_global::<DocumentStore>()?
            .snapshot(self.document.as_ref()?)
            .ok()
    }

    fn panel(id: PanelId, cx: &App) -> Option<Entity<Self>> {
        let dock = CenterPanes::dock(cx)?;
        let panel = dock.read(cx).panel(id)?.clone();
        gpui_kit::component::dock::PanelHandle::of(&panel)?
            .view()
            .downcast::<Self>()
            .ok()
    }

    fn replaceable(&self, cx: &App) -> bool {
        !self.pinned
            && self.saving.is_none()
            && !self.document.as_ref().is_some_and(|key| {
                cx.try_global::<DocumentStore>()
                    .is_some_and(|docs| docs.is_dirty(key))
            })
    }
}

/// The key a file's tab opens under, so the same file opens once.
fn key(session: &str, path: &str) -> String {
    format!(
        "file:{}",
        serde_json::to_string(&(session, path)).expect("file alias")
    )
}

#[cfg(test)]
#[path = "open_file_intent_tests.rs"]
mod open_file_intent_tests;
#[cfg(test)]
#[path = "file_view_source_tests.rs"]
mod source_tests;
