//! The Files tab drawn in the look: the column's quiet icon buttons at the top (show excluded files, sort, new file,
//! refresh), then the tree in the shared file rows, 30 high and stepped in by `INDENT` per level, each with its
//! chevron, its folder or coloured file-type mark, and its name. Rows and controls borrow the pane's owned lazy model
//! and action state.
use super::{FileView, FilesPane, INDENT, Row};
use crate::components::file_row::file_row;
use crate::components::quiet_button::quiet_icon_button;
use crate::core::theme::look::Look;
use crate::core::theme::metrics::{SPACE, TREE_ROW_HEIGHT};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::files as ids;
use crate::core::ui::file_icon::file_icon;
use gpui_kit::assets::IconName;
use gpui_kit::component::menu::ContextMenuExt as _;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{Icon, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

/// A folder's chevron.
const CHEVRON_SIZE: Pixels = px(12.);
/// A folder's mark, the size of a file's.
const MARK_SIZE: Pixels = px(14.);

impl FilesPane {
    /// One line of the tree: a file or folder, a folder still loading or that failed, or "Load more" under a long one.
    fn row(&self, row: Row<'_>, ix: usize, cx: &mut Context<Self>) -> AnyElement {
        let look = Look::of(cx);
        // Where a line at `depth` starts: the row's own padding, then one step in per level.
        let inset = |depth: usize| SPACE[3] + INDENT * depth as f32;
        // A line that only says something: the tree's height and inset, in Meta.
        let note = |depth: usize| {
            h_flex()
                .h(TREE_ROW_HEIGHT)
                .pl(inset(depth))
                .pr(SPACE[3])
                .text_role(TextRole::Meta, cx)
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
                // A folder has its chevron and folder mark; a file a blank where the chevron would be, then the
                // coloured mark for its type.
                let mark = if folder {
                    Icon::new(if open {
                        IconName::FolderOpen
                    } else {
                        IconName::Folder
                    })
                    .size(MARK_SIZE)
                    .text_color(look.text_3)
                    .into_any_element()
                } else {
                    file_icon(format!("tree-mark:{ix}"), &entry.path, cx).into_any_element()
                };
                file_row(ix, picked, cx)
                    .h(TREE_ROW_HEIGHT)
                    .pl(inset(depth))
                    .text_color(if entry.excluded {
                        look.text_3
                    } else {
                        look.text_1
                    })
                    .child(
                        h_flex()
                            .gap(SPACE[2])
                            .min_w_0()
                            .whitespace_nowrap()
                            .child(div().w(CHEVRON_SIZE).flex_none().when(folder, |chevron| {
                                chevron.child(
                                    Icon::new(if open {
                                        IconName::ChevronDown
                                    } else {
                                        IconName::ChevronRight
                                    })
                                    .size(CHEVRON_SIZE)
                                    .text_color(look.text_3),
                                )
                            }))
                            .child(mark)
                            .child(div().min_w_0().truncate().child(entry.name.clone())),
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
                                (None, None),
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
            Row::Loading { depth } => note(depth)
                .text_color(look.text_3)
                .child("Loading…")
                .into_any_element(),
            Row::Failed { depth, message } => note(depth)
                .text_color(look.problem)
                .child(
                    div()
                        .overflow_hidden()
                        .text_ellipsis()
                        .child(message.to_string()),
                )
                .into_any_element(),
            Row::Truncated { depth, dir } => {
                let dir = dir.to_owned();
                file_row(ix, false, cx)
                    .h(TREE_ROW_HEIGHT)
                    .pl(inset(depth))
                    .text_role(TextRole::Meta, cx)
                    .text_color(look.text_2)
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

    /// The column's top line: its actions as the shared quiet icon buttons, each named and with its tip, on the right
    /// where Sessions has its sort and "+".
    fn header(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let excluded = if self.include_excluded {
            "Hide excluded files"
        } else {
            "Show excluded files"
        };
        let sort = if self.descending {
            "Sort A to Z"
        } else {
            "Sort Z to A"
        };
        h_flex()
            .pt(SPACE[5])
            .px(SPACE[4])
            .gap(SPACE[1])
            .justify_end()
            .child(
                quiet_icon_button(
                    "files-excluded",
                    if self.include_excluded {
                        IconName::EyeOff
                    } else {
                        IconName::Eye
                    },
                    cx,
                )
                .control(ids::SHOW_EXCLUDED, excluded)
                .toggled(self.include_excluded)
                .tooltip(excluded)
                .on_click(cx.listener(|pane, _, _, cx| {
                    pane.include_excluded = !pane.include_excluded;
                    pane.refresh(cx);
                })),
            )
            .child(
                quiet_icon_button(
                    "files-sort",
                    if self.descending {
                        IconName::ArrowUpZA
                    } else {
                        IconName::ArrowDownAZ
                    },
                    cx,
                )
                .control(ids::SORT, sort)
                .tooltip(sort)
                .on_click(cx.listener(|pane, _, _, cx| {
                    pane.descending = !pane.descending;
                    pane.refresh(cx);
                })),
            )
            .child(
                quiet_icon_button("files-new", IconName::FilePlus, cx)
                    .control(ids::NEW_FILE, "New file")
                    .tooltip("New file")
                    .on_click(cx.listener(|pane, _, window, cx| {
                        pane.open_action("createFile", String::new(), None, None, window, cx)
                    })),
            )
            .child(
                quiet_icon_button("files-refresh", IconName::RefreshCw, cx)
                    .control(ids::REFRESH, "Refresh")
                    .tooltip("Refresh")
                    .on_click(cx.listener(|pane, _, _, cx| pane.refresh(cx))),
            )
    }
}

impl Render for FilesPane {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let look = Look::of(cx);
        let pane = v_flex()
            .track_focus(&self.focus)
            .on_key_down(cx.listener(Self::key_down))
            .size_full();
        if self.session.is_none() {
            return pane.child(
                div()
                    .px(SPACE[4])
                    .py(SPACE[5])
                    .text_role(TextRole::Meta, cx)
                    .text_color(look.text_3)
                    .child("Pick a session to see its files."),
            );
        }
        let count = self.tree.rows().len();
        pane.child(self.header(cx))
            .children(self.action_error.clone().map(|error| {
                div()
                    .px(SPACE[4])
                    .py(SPACE[2])
                    .text_role(TextRole::Meta, cx)
                    .text_color(look.problem)
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
                .px(SPACE[3])
                .py(SPACE[2]),
            )
    }
}
