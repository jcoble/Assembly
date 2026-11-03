//! File editor hydration and versioned edits over the sole DocumentStore; preserves the owning FileView lifecycle.
use super::{DocumentKey, DocumentSnapshot, DocumentStore, DocumentView, EditorSurface, FileView};
use crate::core::ui::center_panes::CenterPanes;
use gpui_kit::component::dock::PanelId;
use gpui_kit::component::input::InputEvent;
use gpui_kit::*;

impl FileView {
    pub(super) fn bind_editor(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.editor.is_some()
            || !self.active
            || (self.expected_workspace.is_some() && self.owner.is_none())
        {
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
            super::key(&self.session, &self.path),
            current,
            cx,
        );
        if claimed != current {
            if let Some(existing) = Self::panel(claimed, cx) {
                let incoming = cx.entity();
                let jump = self.jump.take();
                let pinned = self.pinned;
                let history_shown = self.history_shown;
                let handle = window.window_handle();
                cx.defer(move |cx| {
                    let _ = handle.update(cx, |_, window, cx| {
                        existing.update(cx, |view, cx| {
                            view.pinned |= pinned;
                            if history_shown && !view.history_shown {
                                view.toggle_history(cx);
                            }
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
        if self.expected_workspace.is_none() {
            crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed(cx);
        }
    }

    pub(super) fn install_editor(
        &mut self,
        snapshot: DocumentSnapshot,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.document = Some(snapshot.key.clone());
        let mut editor = EditorSurface::new(&self.path, &snapshot.text, window, cx);
        if let Some(view) = self.restored_view.take() {
            cx.global_mut::<DocumentStore>()
                .stash_view(&snapshot.key, view);
        }
        if let Some((line, column)) = self.jump.take() {
            editor.jump(line, column, cx);
        } else if let Some(view) = cx
            .global::<DocumentStore>()
            .view_state(self.document.as_ref().expect("document"))
        {
            editor.state.update(cx, |state, cx| {
                state.set_selected_range(
                    super::super::editor_surface::selection(&snapshot.text, view.selection),
                    cx,
                );
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
        crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed(cx);
        cx.notify();
    }

    pub(crate) fn stash_editor_view(&self, cx: &mut App) {
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
}
