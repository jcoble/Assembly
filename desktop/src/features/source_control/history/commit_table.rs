//! The Git history tab's table, the way GitHistoryView.svelte drew it: one 24 px row per commit with the branching
//! picture, then Message, Date, Author and Hash each in a column of their own. Read only: nothing here moves a branch.
use super::inline::{commit_file_menu, commit_tip, files_sentence};
use super::{CommitFiles, CommitTimeline};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::features::source_control::git_labels::{RefKind, commit_age, ref_pills, split_path};
use crate::features::source_control::git_status::{GitStatus, Section};
use crate::features::source_control::graph_lanes::{EdgeKind, LaneRow};
use crate::features::source_control::history_filter::HistoryFilter;
use crate::features::source_control::source_control_pane::ShowSourceControl;
use chrono::{DateTime, Local};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::menu::{ContextMenuExt as _, PopupMenuItem};
use gpui_kit::component::tag::Tag;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

const ROW: f32 = 24.;
const LANE_SPACING: f32 = 12.;
const LANE_OFFSET: f32 = 10.;
/// Never narrower than the "Graph" heading, so with few lanes it can't run into "Message".
const GRAPH_MIN_WIDTH: f32 = 52.;
/// Past this the graph would eat the message column; further lanes fold into the last.
const MAX_DRAWN_LANES: usize = 8;
const DATE_WIDTH: f32 = 156.;
const AUTHOR_WIDTH: f32 = 140.;
const HASH_WIDTH: f32 = 78.;
const COLUMN_GAP: f32 = 10.;
const PAGE: usize = 24;

/// How many lanes the picture needs, capped at what it draws.
pub(super) fn drawn_lanes(rows: &[LaneRow]) -> usize {
    rows.iter()
        .flat_map(|row| {
            std::iter::once(row.lane).chain(row.edges.iter().flat_map(|(_, from, to)| [*from, *to]))
        })
        .max()
        .map_or(0, |lane| (lane + 1).min(MAX_DRAWN_LANES))
}
pub(super) fn graph_width(drawn: usize) -> f32 {
    let lanes = if drawn == 0 {
        0.
    } else {
        LANE_OFFSET * 2. + (drawn - 1) as f32 * LANE_SPACING
    };
    lanes.max(GRAPH_MIN_WIDTH)
}
/// Lanes past the drawn width are pinned to the last one rather than cut off.
fn lane_x(lane: usize, drawn: usize) -> f32 {
    LANE_OFFSET + lane.min(drawn.saturating_sub(1)) as f32 * LANE_SPACING
}
/// "17 Aug 2026 10:04": the same shape every row, so the column lines up. An unreadable time stays as it came.
pub(super) fn committed_on(committed_at: &str) -> String {
    match DateTime::parse_from_rfc3339(committed_at.trim()) {
        Ok(at) => at
            .with_timezone(&Local)
            .format("%d %b %Y %H:%M")
            .to_string(),
        Err(_) => committed_at.to_string(),
    }
}
/// Work that isn't committed, as the row above the newest commit says it.
#[derive(Clone, Debug, PartialEq)]
pub struct Uncommitted {
    pub count_label: String,
    pub detail: String,
}
/// `None` with nothing changed; a status not read yet is no row at all, not an invented clean one.
pub fn uncommitted_of(status: &GitStatus) -> Option<Uncommitted> {
    let count = status.total_files.max(status.files.len());
    if count == 0 {
        return None;
    }
    let detail = [
        (Section::Staged, "Staged"),
        (Section::Changes, "Changed"),
        (Section::Untracked, "New files"),
    ]
    .into_iter()
    .filter_map(|(section, label)| {
        let files = status.section(section).len();
        (files > 0).then(|| format!("{label} {files}"))
    })
    .collect::<Vec<_>>()
    .join(" · ");
    Some(Uncommitted {
        count_label: if count == 1 {
            "1 file".into()
        } else {
            format!("{count} files")
        },
        detail,
    })
}
/// The sentence beside Load more: what the filters left, or how much is read.
pub(super) fn table_footer(
    filter: &HistoryFilter,
    shown: usize,
    loaded: usize,
    reading_more: bool,
    complete: bool,
    pages: u32,
) -> String {
    if shown == 0 {
        "No commit in the history that is loaded matches the current filters.".into()
    } else if let (Some(branch), None, true) = (
        &filter.branch,
        &filter.author,
        filter.text.trim().is_empty(),
    ) {
        format!("Showing {shown} commits from {branch} among {loaded} loaded.")
    } else if filter.active() {
        format!("{shown} of {loaded} loaded commits match the current filters.")
    } else if reading_more {
        "Reading older commits…".into()
    } else {
        super::inline::footer(loaded, complete, pages)
    }
}
/// What the table says with no commits to list, and whether it's a failure. Git's own wording only: matching more
/// eagerly would hide a real failure behind a calm line.
pub(super) fn empty_line(error: Option<&str>, reading: bool) -> (String, bool) {
    match error {
        Some(error) => {
            let lower = error.to_lowercase();
            if lower.contains("not a git repository")
                || lower.contains("must be run in a work tree")
            {
                ("This folder's Git history is unavailable.".into(), false)
            } else {
                (error.to_string(), true)
            }
        }
        None if reading => ("Reading the commit history…".into(), false),
        None => (
            "No commits yet. The first commit you make will appear here.".into(),
            false,
        ),
    }
}
/// A name git couldn't print in plain letters: its quotes, or its octal escapes.
pub(super) fn unreadable_path(path: &str) -> bool {
    let bytes = path.as_bytes();
    path.starts_with('"')
        || path.ends_with('"')
        || bytes.windows(4).any(|part| {
            part[0] == b'\\'
                && (b'0'..=b'3').contains(&part[1])
                && (b'0'..=b'7').contains(&part[2])
                && (b'0'..=b'7').contains(&part[3])
        })
}
fn lane_colors(cx: &App) -> [Hsla; 6] {
    let theme = cx.theme();
    [
        theme.primary,
        theme.success,
        theme.warning,
        theme.danger,
        theme.info,
        theme.muted_foreground,
    ]
}
fn paint_lanes(
    row: &LaneRow,
    drawn: usize,
    colors: [Hsla; 6],
    bounds: Bounds<Pixels>,
    window: &mut Window,
) {
    let at = |x: f32, y: f32| bounds.origin + point(px(x), px(y));
    let middle = ROW / 2.;
    for (kind, from, to) in &row.edges {
        let (x1, x2) = (lane_x(*from, drawn), lane_x(*to, drawn));
        let tone = if *kind == EdgeKind::Child { *to } else { *from };
        let mut path = PathBuilder::stroke(px(1.5));
        match kind {
            EdgeKind::Passing => {
                path.move_to(at(x1, 0.));
                path.line_to(at(x1, ROW));
            }
            EdgeKind::Child => {
                path.move_to(at(x1, 0.));
                path.line_to(at(x2, middle));
            }
            EdgeKind::Parent if from == to => {
                path.move_to(at(x1, middle));
                path.line_to(at(x1, ROW));
            }
            EdgeKind::Parent => {
                path.move_to(at(x1, middle));
                path.cubic_bezier_to(at(x2, ROW), at(x1, middle + 7.), at(x2, ROW - 7.));
            }
        }
        if let Ok(path) = path.build() {
            window.paint_path(path, colors[tone % colors.len()].opacity(0.8));
        }
    }
}
fn graph_cell(row: Option<LaneRow>, width: f32, drawn: usize, cx: &App) -> Div {
    let colors = lane_colors(cx);
    let ground = cx.theme().background;
    let dot = row.as_ref().map(|row| {
        let color = colors[row.lane % colors.len()];
        let radius = if row.merge { 3. } else { 3.5 };
        div()
            .absolute()
            .left(px(lane_x(row.lane, drawn) - radius))
            .top(px(ROW / 2. - radius))
            .size(px(radius * 2.))
            .rounded_full()
            .border_1()
            .border_color(color)
            .bg(if row.merge { ground } else { color })
    });
    div()
        .relative()
        .flex_none()
        .w(px(width))
        .h(px(ROW))
        .when_some(row, |cell, row| {
            cell.child(
                canvas(
                    |_, _, _| {},
                    move |bounds, _, window, _| paint_lanes(&row, drawn, colors, bounds, window),
                )
                .absolute()
                .size_full(),
            )
        })
        .children(dot)
}
fn cell(width: f32, text: String, color: Hsla, cx: &App) -> Div {
    div()
        .flex_none()
        .w(px(width))
        .truncate()
        .text_role(TextRole::Body, cx)
        .text_color(color)
        .child(text)
}
impl CommitTimeline {
    pub(super) fn render_table(&mut self, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme();
        let (muted, border, surface, danger) = (
            theme.muted_foreground,
            theme.border,
            theme.secondary,
            theme.danger,
        );
        let drawn = drawn_lanes(&self.graph);
        let graph = graph_width(drawn);
        let head = h_flex()
            .w_full()
            .flex_none()
            .h(px(ROW))
            .gap(px(COLUMN_GAP))
            .pr(px(COLUMN_GAP))
            .border_b_1()
            .border_color(border)
            .bg(surface)
            .text_role(TextRole::Body, cx)
            .font_weight(FontWeight::SEMIBOLD)
            .text_color(muted)
            .child(div().flex_none().w(px(graph)).child("GRAPH"))
            .child(div().flex_1().min_w_0().child("MESSAGE"))
            .child(
                div()
                    .flex_none()
                    .w(px(DATE_WIDTH))
                    .text_right()
                    .child("DATE"),
            )
            .child(div().flex_none().w(px(AUTHOR_WIDTH)).child("AUTHOR"))
            .child(div().flex_none().w(px(HASH_WIDTH)).child("HASH"));
        let notice = |text: String, color: Hsla| {
            div()
                .px(px(10.))
                .py(px(12.))
                .text_role(TextRole::Body, cx)
                .text_color(color)
                .child(text)
        };
        let body = if self.history.commits.is_empty() {
            let (line, bad) = empty_line(self.error.as_deref(), self.reading);
            notice(line, if bad { danger } else { muted }).into_any_element()
        } else {
            let weak = cx.entity().downgrade();
            list(self.list.clone(), move |at, _, cx| {
                weak.update(cx, |timeline, cx| timeline.table_item(at, cx))
                    .unwrap_or_else(|_| Empty.into_any_element())
            })
            .flex_1()
            .into_any_element()
        };
        v_flex()
            .size_full()
            .min_h_0()
            .child(head)
            .child(body)
            .into_any_element()
    }
    /// One entry of the list: the uncommitted row (empty when there's none), each commit, then the footer.
    fn table_item(&mut self, at: usize, cx: &mut Context<Self>) -> AnyElement {
        if at == 0 {
            return self.uncommitted_row(cx);
        }
        match self.visible.get(at - 1).copied() {
            Some(index) => self.table_row(at - 1, index, cx),
            None => self.table_footer_row(cx),
        }
    }
    fn table_row(&mut self, at: usize, index: usize, cx: &mut Context<Self>) -> AnyElement {
        let commit = self.history.commits[index].clone();
        let expanded = self.expanded.contains(&commit.sha);
        let lanes = self.graph.get(at).cloned();
        let drawn = drawn_lanes(&self.graph);
        let width = graph_width(drawn);
        let graph = graph_cell(lanes, width, drawn, cx);
        let theme = cx.theme();
        let (muted, hover, open) = (
            theme.muted_foreground,
            theme.secondary_hover,
            theme.secondary,
        );
        let mono = theme.mono_font_family.clone();
        let subject = if commit.subject.is_empty() {
            "(no message)".to_string()
        } else {
            commit.subject.clone()
        };
        let pills = ref_pills(&commit.refs).into_iter().map(|pill| {
            let tag = match pill.kind {
                RefKind::Tag => Tag::warning(),
                RefKind::Head | RefKind::Branch | RefKind::Remote => Tag::secondary(),
            };
            let tag = if pill.kind == RefKind::Remote {
                tag.outline()
            } else {
                tag
            };
            tag.xsmall()
                .rounded_full()
                .max_w(px(220.))
                .truncate()
                .child(pill.label)
        });
        let tip = commit_tip(&commit);
        let age = commit_age(&commit.committed_at);
        let date = committed_on(&commit.committed_at);
        let toggled = commit.sha.clone();
        let (menu_toggle, menu_sha, menu_subject) = (
            commit.sha.clone(),
            commit.sha.clone(),
            commit.subject.clone(),
        );
        let view = cx.entity().downgrade();
        let row = h_flex()
            .id(SharedString::from(format!("history-row:{}", commit.sha)))
            .debug_selector({
                let sha = commit.sha.clone();
                move || format!("history-row:{sha}")
            })
            .w_full()
            .h(px(ROW))
            .gap(px(COLUMN_GAP))
            .pr(px(COLUMN_GAP))
            .cursor_pointer()
            .when(expanded, move |row| row.bg(open))
            .hover(move |row| row.bg(hover))
            .tooltip(move |window, cx| Tooltip::new(tip.clone()).build(window, cx))
            .on_click(
                cx.listener(move |timeline, _, _, cx| timeline.toggle_commit(toggled.clone(), cx)),
            )
            .child(graph)
            .child(
                h_flex()
                    .flex_1()
                    .min_w_0()
                    .overflow_hidden()
                    .gap(px(5.))
                    .children(pills)
                    .child(div().min_w_0().truncate().child(subject)),
            )
            .child(
                div()
                    .id(SharedString::from(format!("history-date:{}", commit.sha)))
                    .flex_none()
                    .w(px(DATE_WIDTH))
                    .truncate()
                    .text_right()
                    .text_role(TextRole::Body, cx)
                    .text_color(muted)
                    .tooltip(move |window, cx| Tooltip::new(age.clone()).build(window, cx))
                    .child(date),
            )
            .child(cell(AUTHOR_WIDTH, commit.author.clone(), muted, cx))
            .child(cell(HASH_WIDTH, commit.short_sha.clone(), muted, cx).font_family(mono))
            .context_menu(move |menu, _, _| {
                let (toggle, sha, subject, view) = (
                    menu_toggle.clone(),
                    menu_sha.clone(),
                    menu_subject.clone(),
                    view.clone(),
                );
                menu.item(
                    PopupMenuItem::new(if expanded {
                        "Hide changed files"
                    } else {
                        "View changed files"
                    })
                    .on_click(move |_, _, cx| {
                        _ = view.update(cx, |timeline, cx| {
                            timeline.toggle_commit(toggle.clone(), cx)
                        });
                    }),
                )
                .item(
                    PopupMenuItem::new("Copy commit hash").on_click(move |_, _, cx| {
                        cx.write_to_clipboard(ClipboardItem::new_string(sha.clone()))
                    }),
                )
                .item(
                    PopupMenuItem::new("Copy subject").on_click(move |_, _, cx| {
                        cx.write_to_clipboard(ClipboardItem::new_string(subject.clone()))
                    }),
                )
            });
        let mut block = v_flex().w_full().min_w_0().child(row);
        if expanded {
            block = block.child(self.table_files(&commit, width, cx));
        }
        block.into_any_element()
    }
}

#[path = "commit_table_ends.rs"]
mod ends;
#[path = "commit_table_files.rs"]
mod files;

#[cfg(test)]
#[path = "commit_table_tests.rs"]
mod tests;
