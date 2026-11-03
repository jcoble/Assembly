//! One source editor per opened file, backed by the bounded shared draft store and owning-host bulk file calls.

use gpui_kit::component::dock::{
    PanelId, PanelInfo, panel_handle, register_panel,
};
use gpui_kit::component::input::InputEvent;
use gpui_kit::*;
use serde_json::{Value, json};

use super::document_store::{DocumentKey, DocumentSnapshot, DocumentStore, DocumentView};
use super::editor_surface::EditorSurface;
use super::entry::{FileChunk, FileOwnership};
use super::file_history::FileHistory;
use super::file_save::{ConfirmedFileSave, FileSaveController};

#[path = "file_view_presentation.rs"]
mod presentation;
#[path = "file_view_conflict.rs"]
mod conflict;
#[path = "file_view_save.rs"]
mod saving;
use super::file_text::FileText;
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::state::preferences::Preferences;
use crate::core::ui::center_panes::CenterPanes;

#[cfg(test)]
#[path = "file_view_tests.rs"]
mod tests;

/// The pane's name in the saved layout.
pub const FILE: &str = "File";
actions!(files, [SaveDocument]);

pub struct FileView {
    focus: FocusHandle,
    pub(super) session: String,
    pub(super) path: String,
    pub(super) text: FileText,
    /// The part being read; dropping it cancels its call.
    pub(super) loading: Option<Task<()>>,
    started: bool,
    owner: Option<FileOwnership>,
    document: Option<DocumentKey>,
    editor: Option<EditorSurface>,
    editor_events: Option<Subscription>,
    editor_error: Option<String>,
    active: bool,
    jump: Option<(u32, u32)>,
    pinned: bool,
    save: Option<FileSaveController>,
    saving: Option<Task<()>>,
    upload: Option<String>,
    last_save: Option<ConfirmedFileSave>,
    history: Option<Entity<FileHistory>>,
    comparison: Option<Entity<super::file_comparison::FileComparison>>,
    history_shown: bool,
    prompting: Option<Task<()>>,
    reload_version: Option<u64>,
    /// Redraws in the editor's face, size and line height when Settings changes them.
    _preferences: Option<Subscription>,
}

impl FileView {
    /// Restores a file tab from the saved layout.
    pub fn register(cx: &mut App) {
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
        cx.bind_keys([save.expect("the save-file binding parses")]);
        register_panel(cx, FILE, |build, _, cx| {
            let saved = match build.state().info.clone() {
                PanelInfo::Panel(saved) => saved,
                _ => Value::Null,
            };
            let text = |key: &str| saved[key].as_str().unwrap_or_default().to_string();
            let (session, path) = (text("session"), text("path"));
            let view = cx.new(|cx| FileView::new(session.clone(), path.clone(), cx));
            CenterPanes::remember(key(&session, &path), PanelId::from(view.entity_id()), cx);
            panel_handle(view)
        });
    }

    /// Opens the session's file at `path` in the center, or brings its tab forward.
    pub fn open(session: String, path: String, window: &mut Window, cx: &mut App) {
        Self::open_at(session, path, None, None, false, window, cx);
    }

    pub fn new(session: String, path: String, cx: &mut Context<Self>) -> Self {
        FileView {
            focus: cx.focus_handle(),
            session,
            path,
            text: FileText::default(),
            loading: None,
            started: false,
            owner: None,
            document: None,
            editor: None,
            editor_events: None,
            editor_error: None,
            active: false,
            jump: None,
            pinned: true,
            save: None,
            saving: None,
            upload: None,
            last_save: None,
            history: None,
            comparison: None,
            history_shown: false,
            prompting: None,
            reload_version: None,
            _preferences: Preferences::try_global(cx)
                .map(|preferences| cx.observe(&preferences, |_, _, cx| cx.notify())),
        }
    }

    pub(super) fn load_part(&mut self, offset: u64, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_bulk(cx) else {
            self.text.failed("The backend isn't running.".into());
            return;
        };
        let args = json!({ "session": self.session, "path": self.path, "offset": offset,
            "revision": self.text.revision() });
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
        let Some(socket) = SocketClient::try_global(cx) else {
            self.text.failed("The backend isn't running.".into());
            return;
        };
        let call = socket.read(cx).call::<FileOwnership>(
            "files",
            "owner",
            json!({ "session": self.session }),
            None,
        );
        self.loading = Some(cx.spawn(async move |view, cx| {
            let answer = call.await;
            let _ = view.update(cx, |view, cx| {
                view.loading = None;
                if !view.active {
                    return;
                }
                match answer {
                    Outcome::Ok(owner) => {
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
                        view.owner = Some(owner);
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

    fn bind_editor(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.editor.is_some() || !self.active {
            return;
        }
        if self.reload_version.is_none()
            && let Some(key) = &self.document
            && let Ok(snapshot) = cx.global::<DocumentStore>().snapshot(key)
        {
            self.install_editor(snapshot, window, cx);
            return;
        }
        if self.text.error.is_some()
            && self.reload_version.take().is_some()
            && let Some(key) = &self.document
            && let Ok(snapshot) = cx.global::<DocumentStore>().snapshot(key)
        {
            self.install_editor(snapshot, window, cx);
            return;
        }
        let (Some(owner), Some(text), Some(revision)) =
            (&self.owner, self.text.content(), self.text.revision())
        else {
            return;
        };
        let key = DocumentKey {
            machine: owner.machine.clone(),
            checkout_root: owner.checkout_root.clone(),
            path: self.path.clone(),
        };
        if !cx.has_global::<DocumentStore>() {
            cx.set_global(DocumentStore::default());
        }
        if let Err(error) =
            cx.global_mut::<DocumentStore>()
                .open(key.clone(), revision.to_owned(), text.to_owned())
        {
            self.editor_error = Some(format!(
                "This file cannot be edited: {error:?}. Existing drafts are preserved."
            ));
            return;
        }
        if let Some(version) = self.reload_version.take() {
            if let Err(error) = cx.global_mut::<DocumentStore>().reload(
                &key,
                version,
                revision.to_owned(),
                text.to_owned(),
            ) {
                self.editor_error = Some(format!(
                    "Reload was refused: {error:?}. Your current draft is preserved."
                ));
            }
        }
        let snapshot = cx
            .global::<DocumentStore>()
            .snapshot(&key)
            .expect("opened document");
        let current = PanelId::from(cx.entity_id());
        let document_key = format!(
            "document:{}",
            serde_json::to_string(&(&key.machine, &key.checkout_root, &key.path))
                .expect("document key")
        );
        let claimed = CenterPanes::claim_file(
            document_key,
            self::key(&self.session, &self.path),
            current,
            cx,
        );
        if claimed != current {
            if let Some(existing) = Self::panel(claimed, cx) {
                let incoming = cx.entity();
                let jump = self.jump.take();
                let pinned = self.pinned;
                let handle = window.window_handle();
                cx.defer(move |cx| {
                    let _ = handle.update(cx, |_, window, cx| {
                        existing.update(cx, |view, cx| {
                            view.pinned |= pinned;
                            if let Some((line, column)) = jump {
                                if let Some(editor) = &mut view.editor {
                                    editor.jump(line, column, cx);
                                } else {
                                    view.jump = Some((line, column));
                                }
                            }
                        });
                        if let Some(dock) = CenterPanes::dock(cx) {
                            dock.update(cx, |dock, cx| {
                                dock.remove_panel(incoming, window, cx);
                                dock.select_panel(claimed, window, cx);
                            });
                        }
                    });
                });
            }
            return;
        }
        self.document = Some(key);
        self.install_editor(snapshot, window, cx);
        if !self.pinned {
            if let Some(old) = CenterPanes::file_preview(cx).filter(|id| *id != current)
                && let Some(previous) = Self::panel(old, cx)
            {
                let handle = window.window_handle();
                cx.defer(move |cx| {
                    let _ = handle.update(cx, |_, window, cx| {
                        if previous.read(cx).replaceable(cx)
                            && let Some(dock) = CenterPanes::dock(cx)
                        {
                            dock.update(cx, |dock, cx| dock.remove_panel(previous, window, cx));
                        }
                    });
                });
            }
            CenterPanes::set_file_preview(current, cx);
        }
    }

    fn install_editor(
        &mut self,
        snapshot: DocumentSnapshot,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.document = Some(snapshot.key.clone());
        let mut editor = EditorSurface::new(&self.path, &snapshot.text, window, cx);
        if let Some((line, column)) = self.jump.take() {
            editor.jump(line, column, cx);
        } else if let Some(view) = cx
            .global::<DocumentStore>()
            .view_state(self.document.as_ref().expect("document"))
        {
            editor.state.update(cx, |state, cx| {
                state.set_selected_range(view.selection, cx);
                state.set_scroll_offset(point(px(view.scroll.0), px(view.scroll.1)), cx);
            });
        }
        self.editor_events = Some(cx.subscribe_in(
            &editor.state,
            window,
            |view, _, event: &InputEvent, window, cx| {
                if let InputEvent::Change = event {
                    view.editor_changed(window, cx);
                }
            },
        ));
        self.editor = Some(editor);
        self.text.release_content();
    }

    fn editor_changed(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let (Some(editor), Some(key)) = (&self.editor, &self.document) else {
            return;
        };
        let value = editor.state.read(cx).value().to_string();
        let snapshot = cx
            .global::<DocumentStore>()
            .snapshot(key)
            .expect("loaded document");
        match cx
            .global_mut::<DocumentStore>()
            .replace(key, snapshot.version, value)
        {
            Ok(_) => {
                self.pinned = true;
                CenterPanes::pin_file(PanelId::from(cx.entity_id()), cx);
                self.editor_error = None;
            }
            Err(error) => {
                editor
                    .state
                    .update(cx, |state, cx| state.set_value(snapshot.text, window, cx));
                self.editor_error = Some(format!(
                    "The edit was refused: {error:?}. The existing draft is preserved."
                ));
            }
        }
        cx.notify();
    }

    pub fn open_at(
        session: String,
        path: String,
        line: Option<u32>,
        column: Option<u32>,
        pinned: bool,
        window: &mut Window,
        cx: &mut App,
    ) {
        let tab_key = key(&session, &path);
        let id = CenterPanes::open(tab_key, window, cx, |_, cx| {
            cx.new(|cx| {
                let mut view = FileView::new(session, path, cx);
                view.pinned = pinned;
                view
            })
        });
        if let Some(id) = id
            && let Some(view) = Self::panel(id, cx)
        {
            view.update(cx, |view, cx| {
                view.pinned |= pinned;
                if let Some(line) = line {
                    if let Some(editor) = &mut view.editor {
                        editor.jump(line, column.unwrap_or(1), cx);
                    } else {
                        view.jump = Some((line, column.unwrap_or(1)));
                    }
                }
                cx.notify();
            });
        }
    }

    fn toggle_history(&mut self, cx: &mut Context<Self>) {
        self.history_shown = !self.history_shown;
        if self.history.is_none() {
            self.history =
                Some(cx.new(|_| FileHistory::new(self.session.clone(), self.path.clone())));
        }
        if let Some(history) = &self.history {
            history.update(cx, |history, cx| {
                history.set_active(self.active && self.history_shown, cx)
            });
        }
        cx.notify();
    }

    fn stash_editor_view(&self, cx: &mut App) {
        if let (Some(key), Some(editor)) = (&self.document, &self.editor) {
            let state = editor.state.read(cx);
            let selection = state.selected_range();
            let scroll = state.scroll_offset();
            cx.global_mut::<DocumentStore>().stash_view(
                key,
                DocumentView {
                    selection,
                    scroll: (f32::from(scroll.x), f32::from(scroll.y)),
                },
            );
        }
    }

    fn release_read(&mut self, cx: &mut Context<Self>) {
        if let (Some(socket), Some(revision)) = (SocketClient::try_bulk(cx), self.text.revision()) {
            socket
                .read(cx)
                .call::<Value>(
                    "files",
                    "release",
                    json!({ "session": self.session, "path": self.path,
                "revision": revision }),
                    None,
                )
                .ignore_reply();
        }
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
