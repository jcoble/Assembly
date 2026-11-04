//! Native tree rows and controls borrow the pane's owned lazy model and action state.
use super::{FileView, FilesPane, INDENT, Row, TREE_LINE};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::menu::ContextMenuExt as _;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

impl FilesPane {
    fn row(&self, row: Row<'_>, ix: usize, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme();
        let (muted, danger) = (theme.muted_foreground, theme.danger);
        let line = |depth: usize| {
            h_flex()
                .h(TREE_LINE)
                .pl(px(8.) + INDENT * depth as f32)
                .pr_2()
                .gap_1()
                .text_role(TextRole::Title, cx)
                .line_height(TREE_LINE)
                .whitespace_nowrap()
                .overflow_hidden()
        };
        match row {
            Row::Entry { entry, depth, open } => {
                let picked = self.selected.as_deref() == Some(entry.path.as_str());
                let (path, folder) = (entry.path.clone(), entry.is_directory);
                let full_path = path.clone();
                let menu_path = path.clone();
                let row_session = self.session.clone();
                let row_source = self.source.clone();
                let menu_session = self.session.clone();
                let menu_source = self.source.clone();
                let pane = cx.entity().downgrade();
                let (accent, secondary, foreground) =
                    (theme.accent, theme.secondary, theme.foreground);
                let glyph = match (folder, open) {
                    (true, true) => IconName::FolderOpen,
                    (true, false) => IconName::Folder,
                    _ => IconName::File,
                };
                line(depth)
                    .id(ix)
                    .rounded(px(6.))
                    .cursor_pointer()
                    .text_color(if entry.excluded { muted } else { foreground })
                    .when(picked, |row| row.bg(secondary))
                    .when(!picked, |row| row.hover(|row| row.bg(accent)))
                    .child(div().w(px(12.)).flex_none().when(folder, |chevron| {
                        chevron.child(
                            Icon::new(if open {
                                IconName::ChevronDown
                            } else {
                                IconName::ChevronRight
                            })
                            .size(px(12.))
                            .text_color(muted),
                        )
                    }))
                    .child(Icon::new(glyph).size(px(14.)).text_color(muted))
                    .child(
                        div()
                            .min_w_0()
                            .overflow_hidden()
                            .text_ellipsis()
                            .child(entry.name.clone()),
                    )
                    .tooltip(move |window, cx| Tooltip::new(full_path.clone()).build(window, cx))
                    .on_click(cx.listener(move |pane, event: &ClickEvent, window, cx| {
                        if pane.session != row_session || pane.source != row_source {
                            return;
                        }
                        window.focus(&pane.focus, cx);
                        pane.clicked(path.clone(), folder, cx);
                        if let (false, Some(session)) = (folder, pane.session.clone()) {
                            FileView::open_at(
                                session,
                                path.clone(),
                                None,
                                None,
                                event.click_count() > 1,
                                row_source.clone(),
                                window,
                                cx,
                            );
                        }
                    }))
                    .context_menu(move |menu, _, cx| {
                        if let Some(session) = &menu_session {
                            Self::file_menu(
                                menu,
                                pane.clone(),
                                session.clone(),
                                menu_path.clone(),
                                folder,
                                menu_source.clone(),
                                cx,
                            )
                        } else {
                            menu
                        }
                    })
                    .into_any_element()
            }
            Row::Loading { depth } => line(depth)
                .text_color(muted)
                .child("Loading…")
                .into_any_element(),
            Row::Failed { depth, message } => line(depth)
                .text_color(danger)
                .child(
                    div()
                        .overflow_hidden()
                        .text_ellipsis()
                        .child(message.to_string()),
                )
                .into_any_element(),
            Row::Truncated { depth, dir } => {
                let dir = dir.to_owned();
                line(depth)
                    .id(ix)
                    .text_color(muted)
                    .cursor_pointer()
                    .child("Load more")
                    .on_click(cx.listener(move |pane, _, _, cx| {
                        if let Some(cursor) = pane.tree.more(&dir) {
                            pane.load(dir.clone(), Some(cursor), cx);
                        }
                    }))
                    .into_any_element()
            }
        }
    }
}

impl Render for FilesPane {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let muted = cx.theme().muted_foreground;
        let pane = v_flex()
            .track_focus(&self.focus)
            .on_key_down(cx.listener(Self::key_down))
            .size_full();
        if self.session.is_none() {
            return pane.child(
                div()
                    .px_3()
                    .py_2()
                    .text_role(TextRole::Body, cx)
                    .text_color(muted)
                    .child("Pick a session to see its files."),
            );
        }
        let count = self.tree.rows().len();
        pane.child(
            h_flex()
                .px_2()
                .py_1()
                .gap_1()
                .child(
                    Button::new("files-excluded")
                        .ghost()
                        .label(if self.include_excluded {
                            "Hide excluded"
                        } else {
                            "Show excluded"
                        })
                        .on_click(cx.listener(|pane, _, _, cx| {
                            pane.include_excluded = !pane.include_excluded;
                            pane.refresh(cx);
                        })),
                )
                .child(
                    Button::new("files-sort")
                        .ghost()
                        .label(if self.descending { "Z–A" } else { "A–Z" })
                        .on_click(cx.listener(|pane, _, _, cx| {
                            pane.descending = !pane.descending;
                            pane.refresh(cx);
                        })),
                )
                .child(
                    Button::new("files-new")
                        .ghost()
                        .label("New file")
                        .on_click(cx.listener(|pane, _, window, cx| {
                            pane.open_action("createFile", String::new(), None, None, window, cx)
                        })),
                )
                .child(
                    Button::new("files-refresh")
                        .ghost()
                        .label("Refresh")
                        .on_click(cx.listener(|pane, _, _, cx| pane.refresh(cx))),
                ),
        )
        .children(self.action_error.clone().map(|error| {
            div()
                .px_2()
                .text_role(TextRole::Body, cx)
                .text_color(cx.theme().danger)
                .child(error)
        }))
        .child(
            uniform_list(
                "files",
                count,
                cx.processor(|pane, range: std::ops::Range<usize>, _window, cx| {
                    let pane = &*pane;
                    pane.tree
                        .rows()
                        .into_iter()
                        .enumerate()
                        .skip(range.start)
                        .take(range.len())
                        .map(|(ix, row)| pane.row(row, ix, cx))
                        .collect()
                }),
            )
            .track_scroll(&self.list)
            .flex_1()
            .px_1()
            .py_1(),
        )
    }
}
