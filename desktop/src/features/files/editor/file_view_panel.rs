//! The existing file panel and editor presentation; FileView owns its document and operation state.
use super::{CloseDocument, DocumentStore, FILE, FileView, Preferences, SaveDocument, SaveState};
use crate::components::quiet_button::quiet_text_button;
use crate::core::theme::look::Look;
use crate::core::theme::metrics::{CRUMB_HEIGHT, SPACE};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::diff_header::path_parts;
use gpui_kit::component::breadcrumb::{Breadcrumb, BreadcrumbItem};
use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, PanelInfo, PanelState};
use gpui_kit::component::input::Editor;
use gpui_kit::component::{Disableable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

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
        if let Some(history) = &self.history {
            history.update(cx, |history, cx| {
                history.set_active(active && self.history_shown, cx)
            });
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
        if let Some(history) = self.history.take() {
            history.update(cx, |history, cx| history.set_active(false, cx));
        }
    }

    fn dump(&self, _: &App) -> PanelState {
        PanelState {
            panel_name: FILE.into(),
            children: Vec::new(),
            info: PanelInfo::panel(self.saved_info()),
        }
    }
}

impl Panel for FileView {
    /// The file's name, which the tab shows and names itself by ("README.md", "Close README.md").
    fn tab_name(&self, _: &App) -> Option<SharedString> {
        Some(self.file_name())
    }

    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        self.file_name()
    }
}

impl FileView {
    /// The last part of the file's path: `src/limiter.ts` is "limiter.ts".
    fn file_name(&self) -> SharedString {
        SharedString::from(
            self.path
                .rsplit('/')
                .next()
                .unwrap_or(&self.path)
                .to_string(),
        )
    }

    /// The file's actions as quiet worded buttons: Save (Check save while the last save's result is unknown), Close,
    /// Reload, Compare, Overwrite while a comparison is open, and History when the file has any.
    fn actions(&self, cx: &mut Context<Self>) -> Div {
        h_flex()
            .flex_none()
            .ml_auto()
            .gap(SPACE[3])
            .child(
                quiet_text_button(
                    "file-save",
                    if self
                        .save
                        .as_ref()
                        .is_some_and(|s| s.state == SaveState::Unknown)
                    {
                        "Check save"
                    } else {
                        "Save"
                    },
                    cx,
                )
                .disabled(self.saving.is_some() || self.document.is_none() || self.locked)
                .on_click(cx.listener(|view, _, _, cx| view.save_document(cx))),
            )
            .child(
                quiet_text_button("file-close", "Close", cx)
                    .disabled(self.saving.is_some())
                    .on_click(cx.listener(|view, _, window, cx| view.request_close(window, cx))),
            )
            .child(
                quiet_text_button("file-reload", "Reload", cx)
                    .disabled(self.document.is_none() || self.saving.is_some())
                    .on_click(cx.listener(|view, _, window, cx| view.request_reload(window, cx))),
            )
            .child(
                quiet_text_button("file-compare", "Compare", cx)
                    .disabled(self.document.is_none() || self.saving.is_some() || self.locked)
                    .on_click(cx.listener(|view, _, _, cx| view.compare_document(cx))),
            )
            .when(self.comparison.is_some(), |row| {
                row.child(
                    quiet_text_button("file-overwrite", "Overwrite", cx)
                        .disabled(self.saving.is_some() || self.locked)
                        .on_click(
                            cx.listener(|view, _, window, cx| view.request_overwrite(window, cx)),
                        ),
                )
            })
            .when(self.has_history(), |row| {
                row.child(
                    quiet_text_button(
                        "file-history",
                        if self.history_shown {
                            "Hide history"
                        } else {
                            "History"
                        },
                        cx,
                    )
                    .debug_selector(|| "file-history".into())
                    .disabled(self.document.is_none())
                    .on_click(cx.listener(|view, _, _, cx| view.toggle_history(cx))),
                )
            })
    }
}

impl Render for FileView {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        self.bind_editor(window, cx);
        let look = Look::of(cx);
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
            // The file's crumb, as the chat's: 46 high under a hairline, the path as the kit's breadcrumb (its folder
            // in text-3, the name in text-1 at 600), what is happening to it in Meta, then its actions as quiet
            // worded buttons at the right. Each part takes the width its words measure; when the tab is too narrow
            // for both, the buttons move to a second line rather than being cut, and only then does the folder
            // shorten.
            .child(
                h_flex()
                    .flex_none()
                    .flex_wrap()
                    .min_h(CRUMB_HEIGHT)
                    .py(SPACE[2])
                    .gap_x(SPACE[3])
                    .gap_y(SPACE[2])
                    .pl(SPACE[6])
                    .pr(SPACE[3])
                    .border_b_1()
                    .border_color(look.hairline)
                    .text_role(TextRole::Body, cx)
                    .text_color(look.text_2)
                    .child(
                        h_flex()
                            .flex_grow(1.)
                            .min_w_0()
                            .gap(SPACE[3])
                            .child(crumb(&self.path, cx))
                            .child(
                                h_flex()
                                    .flex_none()
                                    .gap(SPACE[2])
                                    .text_role(TextRole::Meta, cx)
                                    .text_color(look.text_3)
                                    .children(self.status_words())
                                    .when(dirty, |row| row.child("Unsaved")),
                            ),
                    )
                    .child(self.actions(cx)),
            )
            .children(self.lock_banner(cx))
            .children(status.map(|text| {
                div()
                    .px(SPACE[6])
                    .py(SPACE[5])
                    .text_role(TextRole::Meta, cx)
                    .text_color(look.text_3)
                    .child(text)
            }))
            .children(self.comparison.clone())
            .when_some(self.editor.as_ref(), |view, editor| {
                view.child(
                    Editor::new(&editor.state)
                        .readonly(self.locked)
                        .font_family(Preferences::editor_text(cx).0)
                        .text_size(Preferences::editor_text(cx).1)
                        .size_full(),
                )
            })
            .when(self.history_shown, |view| {
                view.children(self.history.clone())
            })
    }
}

/// The path as the kit's breadcrumb: its folder, which ends in "…" when the tab is narrow, then the name at 600.
fn crumb(path: &str, cx: &App) -> Breadcrumb {
    let (folder, name) = path_parts(path);
    let folder = folder.trim_end_matches('/');
    Breadcrumb::new()
        .min_w_0()
        .overflow_hidden()
        .text_role(TextRole::Body, cx)
        .when(!folder.is_empty(), |crumb| {
            crumb.child(BreadcrumbItem::new(folder.to_string()).min_w_0().truncate())
        })
        .child(
            BreadcrumbItem::new(name.to_string())
                .flex_none()
                .font_weight(FontWeight::SEMIBOLD),
        )
}
