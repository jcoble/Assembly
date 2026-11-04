//! The commit list as Source control draws it: under the sections in the panel's own scroller, a row per commit with its
//! ref pills, opening onto its files, and more only on Load more. The history tab keeps the graph.
use super::super::git_history::GitCommit;
use super::super::git_labels::{RefKind, commit_age, ref_pills, split_path};
use super::super::section_heading::heading_label;
use super::{CommitFiles, CommitTimeline};
use crate::core::intents::{open_commit_diff::OpenCommitDiff, open_file::OpenFile};
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::list::ListItem;
use gpui_kit::component::menu::{ContextMenuExt as _, PopupMenu, PopupMenuItem};
use gpui_kit::component::tag::Tag;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

const CURRENT_FILE_ELSEWHERE: &str = "Switch back to the session folder to open it.";
const MERGE_HAS_NO_CHANGES: &str =
    "This is a merge, and a merge brings no changes of its own. Open the commits it brought in to see the work.";
/// What an open commit says instead of a file list; every case ends in words.
pub(super) fn files_sentence(files: Option<&CommitFiles>, merge: bool) -> Option<String> {
    match files {
        Some(CommitFiles::Reading) | None => Some("Reading what this commit changed…".into()),
        Some(CommitFiles::Failed(error)) => Some(error.clone()),
        Some(CommitFiles::Read(files)) if files.is_empty() => Some(if merge {
            MERGE_HAS_NO_CHANGES.into()
        } else {
            "This commit changed no files.".into()
        }),
        Some(CommitFiles::Read(_)) => None,
    }
}
/// The hover's one fact per line.
pub(super) fn commit_tip(commit: &GitCommit) -> String {
    let age = commit_age(&commit.committed_at);
    let byline = if age.is_empty() {
        commit.author.clone()
    } else {
        format!("{} · {age}", commit.author)
    };
    let refs = commit.refs.join(", ");
    let id = if refs.is_empty() {
        format!("Commit {}", commit.short_sha)
    } else {
        format!("Commit {} · {refs}", commit.short_sha)
    };
    format!("{}\n{byline}\n{id}", commit.subject)
}
pub(super) fn letter_tone(letter: char, cx: &App) -> Hsla {
    let theme = cx.theme();
    match letter {
        'A' => theme.success,
        'D' => theme.danger,
        'R' | 'C' => theme.primary,
        _ => theme.warning,
    }
}
/// A commit file's menu. A name git couldn't print can't be opened, only copied.
pub(super) fn commit_file_menu(
    menu: PopupMenu,
    open: OpenCommitDiff,
    current: Option<OpenFile>,
    path: String,
    readable: bool,
) -> PopupMenu {
    menu.item(
        PopupMenuItem::new("Open diff")
            .disabled(!readable)
            .on_click(move |_, window, cx| window.dispatch_action(Box::new(open.clone()), cx)),
    )
    .item(match current {
        Some(current) => PopupMenuItem::new("Open current file in editor")
            .disabled(!readable)
            .on_click(move |_, window, cx| window.dispatch_action(Box::new(current.clone()), cx)),
        // A disabled row shows no tooltip, so the reason is on its second line.
        None => PopupMenuItem::element(|_, cx| {
            v_flex().child("Open current file in editor").child(
                div()
                    .text_sm()
                    .text_color(cx.theme().muted_foreground)
                    .child(CURRENT_FILE_ELSEWHERE),
            )
        })
        .disabled(true),
    })
    .item(PopupMenuItem::new("Copy path").on_click(move |_, _, cx| {
        cx.write_to_clipboard(ClipboardItem::new_string(path.clone()))
    }))
}
/// "N files", or "No files" for a commit that touched none.
pub(super) fn files_summary(count: usize) -> String {
    match count {
        0 => "No files".into(),
        1 => "1 file".into(),
        many => format!("{many} files"),
    }
}
pub(super) fn count_label(count: usize, complete: bool) -> String {
    if complete {
        count.to_string()
    } else {
        format!("{count} so far")
    }
}
pub(super) fn footer(count: usize, complete: bool, pages: u32) -> String {
    if count == 0 {
        String::new()
    } else if !complete {
        format!("Showing {count} so far.")
    } else if pages > 1 {
        format!("Showing all {count} commits loaded across pages.")
    } else {
        format!("Showing all {count} commits.")
    }
}
impl CommitTimeline {
    pub(super) fn render_inline(&mut self, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme();
        let (muted, danger) = (theme.muted_foreground, theme.danger);
        let line = |text: String, color: Hsla| div().px_2().py_1().text_sm().text_color(color).child(text);
        let complete = self.history.next_cursor.is_none();
        let count = self.history.commits.len();
        let heading = h_flex()
            .px_2()
            .pt_2()
            .gap_2()
            .child(heading_label(
                "Commits",
                (count > 0 || !self.reading).then(|| count_label(count, complete)),
                cx,
            ))
            .child(div().flex_1())
            .child(
                Button::new("commits-refresh")
                    .ghost()
                    .compact()
                    .icon(Icon::new(IconName::RefreshCw).size(px(14.)))
                    .tooltip("Refresh the commit list")
                    .disabled(self.reading)
                    .on_click(cx.listener(|timeline, _, _, cx| timeline.refresh(cx))),
            );
        let body = if let Some(error) = self.error.clone().filter(|_| count == 0) {
            line(error, danger).into_any_element()
        } else if count == 0 {
            line(
                if self.reading {
                    "Reading the history…".to_string()
                } else {
                    "This repository has no commits yet.".to_string()
                },
                muted,
            )
            .into_any_element()
        } else {
            let rows: Vec<AnyElement> = (0..count).map(|index| self.inline_row(index, cx)).collect();
            v_flex().min_w_0().children(rows).into_any_element()
        };
        let reading_more = self.reading && count > 0;
        v_flex()
            .min_w_0()
            .gap_1()
            .child(heading)
            .child(body)
            .when(count > 0 && self.error.is_some(), |view| {
                view.children(self.error.clone().map(|error| line(error, danger)))
            })
            .when(
                !footer(count, complete, self.pages).is_empty(),
                |view| view.child(line(footer(count, complete, self.pages), muted)),
            )
            .when(!complete, |view| {
                view.child(
                    div().px_2().child(
                        Button::new("commits-more")
                            .ghost()
                            .compact()
                            .label(if reading_more {
                                "Reading older commits…"
                            } else {
                                "Load more"
                            })
                            .disabled(reading_more)
                            .on_click(cx.listener(|timeline, _, _, cx| timeline.load_more(cx))),
                    ),
                )
            })
            .into_any_element()
    }
    fn inline_row(&mut self, index: usize, cx: &mut Context<Self>) -> AnyElement {
        let commit = self.history.commits[index].clone();
        let expanded = self.expanded.contains(&commit.sha);
        let theme = cx.theme();
        let muted = theme.muted_foreground;
        let age = commit_age(&commit.committed_at);
        let byline = if age.is_empty() {
            commit.author.clone()
        } else {
            format!("{} · {age}", commit.author)
        };
        let tip = commit_tip(&commit);
        let toggled = commit.sha.clone();
        let menu_toggle = commit.sha.clone();
        let menu_copy = commit.sha.clone();
        let view = cx.entity().downgrade();
        let row = ListItem::new(SharedString::from(format!("commit:{}", commit.sha)))
            .tooltip(move |window, cx| Tooltip::new(tip.clone()).build(window, cx))
            .on_click(cx.listener(move |timeline, _, _, cx| {
                timeline.toggle_commit(toggled.clone(), cx)
            }))
            .child(
                h_flex()
                    .w_full()
                    .min_w_0()
                    .gap_1()
                    .child(
                        Icon::new(if expanded {
                            IconName::ChevronDown
                        } else {
                            IconName::ChevronRight
                        })
                        .size(px(14.))
                        .text_color(muted),
                    )
                    .children(ref_pills(&commit.refs).into_iter().filter(|pill| pill.kind != RefKind::Remote).map(|pill| {
                        let tag = match pill.kind {
                            RefKind::Head => Tag::success(),
                            RefKind::Tag => Tag::warning(),
                            RefKind::Branch | RefKind::Remote => Tag::secondary(),
                        };
                        tag.xsmall().rounded_full().max_w(relative(0.4)).truncate().child(pill.label)
                    }))
                    .child(div().flex_1().min_w_0().truncate().child(commit.subject.clone()))
                    .child(div().flex_none().text_sm().text_color(muted).child(byline)),
            )
            .context_menu(move |menu, _, _| {
                let (toggle, copy, view) = (menu_toggle.clone(), menu_copy.clone(), view.clone());
                menu.item(PopupMenuItem::new(if expanded { "Hide changed files" } else { "View changed files" }).on_click(
                    move |_, _, cx| {
                        _ = view.update(cx, |timeline, cx| timeline.toggle_commit(toggle.clone(), cx));
                    },
                ))
                .item(PopupMenuItem::new("Copy commit hash").on_click(move |_, _, cx| {
                    cx.write_to_clipboard(ClipboardItem::new_string(copy.clone()))
                }))
            });
        let mut block = v_flex().min_w_0().child(row);
        if expanded {
            block = block.child(self.inline_files(&commit, cx));
        }
        block.into_any_element()
    }
    fn inline_files(&self, commit: &GitCommit, cx: &mut Context<Self>) -> AnyElement {
        let sha = commit.sha.as_str();
        let theme = cx.theme();
        let (muted, danger) = (theme.muted_foreground, theme.danger);
        let line = |text: String, color: Hsla| div().px_2().py_1().text_sm().text_color(color).child(text);
        let files = self.files.get(sha);
        let content = match (files_sentence(files, commit.parents.len() > 1), files) {
            (Some(sentence), Some(CommitFiles::Failed(_))) => line(sentence, danger).into_any_element(),
            (Some(sentence), _) => line(sentence, muted).into_any_element(),
            (None, Some(CommitFiles::Read(files))) => v_flex()
                .child(line(files_summary(files.len()), muted))
                .children(files.iter().map(|file| {
                    let (name, folder) = split_path(&file.path);
                    let open = self.commit_diff(sha, &file.path, format!("{} {}", commit.short_sha, commit.subject));
                    let menu_open = open.clone();
                    let current = self.current_file(&file.path);
                    let copied = file.path.clone();
                    let full_path = file.path.clone();
                    let readable = !super::table::unreadable_path(&file.path);
                    let chosen = self.chosen.as_ref().is_some_and(|(at, path)| at == sha && path == &file.path);
                    let (picked_sha, picked_path) = (sha.to_string(), file.path.clone());
                    ListItem::new(SharedString::from(format!("commit-file:{sha}:{}", file.path)))
                        .selected(chosen)
                        .tooltip(move |window, cx| Tooltip::new(full_path.clone()).build(window, cx))
                        .on_click(cx.listener(move |timeline, _, window, cx| {
                            timeline.chosen = Some((picked_sha.clone(), picked_path.clone()));
                            window.dispatch_action(Box::new(open.clone()), cx);
                            cx.notify();
                        }))
                        .child(
                            h_flex()
                                .w_full()
                                .min_w_0()
                                .gap_1()
                                .child(
                                    div()
                                        .flex_none()
                                        .w(px(12.))
                                        .text_sm()
                                        .font_family(cx.theme().mono_font_family.clone())
                                        .text_color(muted)
                                        .child(file.letter.to_string()),
                                )
                                .child(Icon::new(IconName::File).size(px(14.)).text_color(muted))
                                .child(
                                    h_flex()
                                        .flex_1()
                                        .min_w_0()
                                        .gap_1()
                                        .child(div().min_w_0().truncate().child(name.to_string()))
                                        .child(div().min_w_0().truncate().text_sm().text_color(muted).child(folder.to_string())),
                                ),
                        )
                        .context_menu(move |menu, _, _| {
                            commit_file_menu(menu, menu_open.clone(), current.clone(), copied.clone(), readable)
                        })
                        .into_any_element()
                }))
                .into_any_element(),
            (None, _) => Empty.into_any_element(),
        };
        div().pb_1().pl_5().child(content).into_any_element()
    }
}
