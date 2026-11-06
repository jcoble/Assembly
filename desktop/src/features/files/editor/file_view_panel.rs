//! The existing file panel and editor presentation; FileView owns its document and operation state.
use super::{CloseDocument, DocumentStore, FILE, FileView, Preferences, SaveDocument, SaveState};
use crate::core::state::active_file::ActiveFile;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, PanelInfo, PanelState};
use gpui_kit::component::input::Editor;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::json;

impl EventEmitter<PanelEvent> for FileView {}

impl Focusable for FileView {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for FileView {
    fn panel_name(&self) -> &'static str {
        FILE
    }

    fn closable(&self, cx: &App) -> bool {
        self.saving.is_none()
            && !self.document.as_ref().is_some_and(|key| {
                cx.try_global::<DocumentStore>()
                    .is_some_and(|docs| docs.is_dirty(key))
            })
    }

    /// Reads a complete coherent buffer only when shown.
    fn set_active(&mut self, active: bool, _: &mut Window, cx: &mut Context<Self>) {
        if active {
            self.activated_for_workspace(cx);
        }
        self.active = active;
        if active {
            ActiveFile::set(&self.session, &self.path, self.source.clone(), cx);
        } else {
            ActiveFile::clear(&self.session, &self.path, cx);
        }
        if active && !self.started {
            self.started = true;
            self.load_owner(cx);
        }
        if !active {
            self.stash_editor_view(cx);
            self.loading = None;
            self.release_read(cx);
            self.editor_events = None;
            self.editor = None;
            self.comparison = None;
            if self.document.is_none() {
                self.started = false;
            }
        }
        cx.notify();
    }

    fn on_removed(&mut self, _: &mut Window, cx: &mut Context<Self>) {
        self.stash_editor_view(cx);
        crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed(cx);
        if let Some(key) = &self.document {
            cx.global_mut::<DocumentStore>().release_clean(key);
        }
        self.active = false;
        self.loading = None;
        self.saving = None;
        self.drop_upload(cx);
        self.release_read(cx);
        ActiveFile::clear(&self.session, &self.path, cx);
    }

    fn dump(&self, _: &App) -> PanelState {
        PanelState {
            panel_name: FILE.into(),
            children: Vec::new(),
            info: PanelInfo::panel(json!({ "session": self.session, "path": self.path })),
        }
    }
}

impl Panel for FileView {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        SharedString::from(
            self.path
                .rsplit('/')
                .next()
                .unwrap_or(&self.path)
                .to_string(),
        )
    }
}

impl Render for FileView {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        self.bind_editor(window, cx);
        let muted = cx.theme().muted_foreground;
        let dirty = self.document.as_ref().is_some_and(|key| {
            cx.try_global::<DocumentStore>()
                .is_some_and(|docs| docs.is_dirty(key))
        });
        let status = self
            .editor_error
            .clone()
            .or_else(|| self.text.error.clone())
            .or_else(|| {
                if self.text.binary {
                    Some("This file isn't UTF-8 text.".into())
                } else if self.editor.is_none() {
                    Some("Loading…".into())
                } else {
                    None
                }
            });
        v_flex()
            .track_focus(&self.focus)
            .key_context("FileEditor")
            .on_action(cx.listener(|view, _: &SaveDocument, _, cx| view.save_document(cx)))
            .on_action(
                cx.listener(|view, _: &CloseDocument, window, cx| view.request_close(window, cx)),
            )
            .size_full()
            .child(
                h_flex()
                    .px_3()
                    .py_1()
                    .gap_2()
                    .text_role(TextRole::Body, cx)
                    .text_color(muted)
                    .child(self.path.clone())
                    .when(dirty, |row| row.child("Unsaved"))
                    .child(
                        Button::new("file-save")
                            .label(
                                if self
                                    .save
                                    .as_ref()
                                    .is_some_and(|s| s.state == SaveState::Unknown)
                                {
                                    "Check save"
                                } else {
                                    "Save"
                                },
                            )
                            .ghost()
                            .disabled(self.saving.is_some() || self.document.is_none())
                            .on_click(cx.listener(|view, _, _, cx| view.save_document(cx))),
                    )
                    .child(
                        Button::new("file-close")
                            .ghost()
                            .label("Close")
                            .disabled(self.saving.is_some())
                            .on_click(
                                cx.listener(|view, _, window, cx| view.request_close(window, cx)),
                            ),
                    )
                    .child(
                        Button::new("file-reload")
                            .ghost()
                            .label("Reload")
                            .disabled(self.document.is_none() || self.saving.is_some())
                            .on_click(
                                cx.listener(|view, _, window, cx| view.request_reload(window, cx)),
                            ),
                    )
                    .child(
                        Button::new("file-compare")
                            .ghost()
                            .label("Compare")
                            .disabled(self.document.is_none() || self.saving.is_some())
                            .on_click(cx.listener(|view, _, _, cx| view.compare_document(cx))),
                    )
                    .when(self.comparison.is_some(), |row| {
                        row.child(
                            Button::new("file-overwrite")
                                .ghost()
                                .label("Overwrite")
                                .disabled(self.saving.is_some())
                                .on_click(cx.listener(|view, _, window, cx| {
                                    view.request_overwrite(window, cx)
                                })),
                        )
                    }),
            )
            .children(status.map(|text| {
                div()
                    .px_3()
                    .py_2()
                    .text_role(TextRole::Body, cx)
                    .text_color(muted)
                    .child(text)
            }))
            .children(self.comparison.clone())
            .when_some(self.editor.as_ref(), |view, editor| {
                view.child(
                    Editor::new(&editor.state)
                        .font_family(Preferences::editor_text(cx).0)
                        .text_size(Preferences::editor_text(cx).1)
                        .size_full(),
                )
            })
    }
}
