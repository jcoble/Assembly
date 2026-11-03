//! The commit list as Source control draws it: under the sections in the panel's own scroller, a row per commit with its
//! ref pills, opening onto its files, and more only on Load more. The history tab keeps the graph.
use super::super::git_labels::{RefKind, commit_age, ref_pills, split_path};
use super::super::section_heading::heading_label;
use super::{CommitFiles, CommitTimeline};
use crate::core::intents::{open_commit_diff::OpenCommitDiff, open_file::OpenFile};
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::list::ListItem;
use gpui_kit::component::menu::{ContextMenuExt as _, PopupMenuItem};
use gpui_kit::component::tag::Tag;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

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
        let refs = commit.refs.join(", ");
        let tip = format!(
            "{}\n{byline}\n{}",
            commit.subject,
            if refs.is_empty() {
                format!("Commit {}", commit.short_sha)
            } else {
                format!("Commit {} · {refs}", commit.short_sha)
            }
        );
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
                    .children(ref_pills(&commit.refs).into_iter().map(|pill| {
                        let tag = match pill.kind {
                            RefKind::Head => Tag::success(),
                            RefKind::Branch => Tag::secondary(),
                            RefKind::Tag => Tag::warning(),
                        };
                        tag.xsmall().rounded_full().child(pill.label)
                    }))
                    .child(div().flex_1().min_w_0().truncate().text_sm().child(commit.subject.clone()))
                    .child(div().flex_none().text_sm().text_color(muted).child(byline)),
            )
            .context_menu(move |menu, _, _| {
                let (toggle, copy, view) = (menu_toggle.clone(), menu_copy.clone(), view.clone());
                menu.item(PopupMenuItem::new(if expanded { "Hide files" } else { "Show files" }).on_click(
                    move |_, _, cx| {
                        _ = view.update(cx, |timeline, cx| timeline.toggle_commit(toggle.clone(), cx));
                    },
                ))
                .item(PopupMenuItem::new("Copy hash").on_click(move |_, _, cx| {
                    cx.write_to_clipboard(ClipboardItem::new_string(copy.clone()))
                }))
            });
        let mut block = v_flex().min_w_0().child(row);
        if expanded {
            block = block.child(self.inline_files(&commit.sha, &commit.short_sha, &commit.subject, cx));
        }
        block.into_any_element()
    }
    fn inline_files(&self, sha: &str, short_sha: &str, subject: &str, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme();
        let (muted, danger) = (theme.muted_foreground, theme.danger);
        let line = |text: String, color: Hsla| div().px_2().py_1().text_sm().text_color(color).child(text);
        let content = match self.files.get(sha) {
            Some(CommitFiles::Reading) | None => {
                line("Reading what this commit changed…".into(), muted).into_any_element()
            }
            Some(CommitFiles::Failed(error)) => line(error.clone(), danger).into_any_element(),
            Some(CommitFiles::Read(files)) if files.is_empty() => {
                line("This commit changed no files.".into(), muted).into_any_element()
            }
            Some(CommitFiles::Read(files)) => v_flex()
                .child(line(files_summary(files.len()), muted))
                .children(files.iter().map(|file| {
                    let (name, folder) = split_path(&file.path);
                    let open = OpenCommitDiff {
                        session_id: self.session.clone(),
                        expected_source: self.expected_source.clone(),
                        sha: sha.to_string(),
                        path: file.path.clone(),
                        title: format!("{short_sha} {subject}"),
                    };
                    let menu_open = open.clone();
                    let current = OpenFile {
                        session_id: self.session.clone(),
                        path: file.path.clone(),
                        expected_source: self.expected_source.clone(),
                        line: None,
                        column: None,
                        pinned: false,
                    };
                    let copied = file.path.clone();
                    let full_path = file.path.clone();
                    ListItem::new(SharedString::from(format!("commit-file:{sha}:{}", file.path)))
                        .tooltip(move |window, cx| Tooltip::new(full_path.clone()).build(window, cx))
                        .on_click(move |_, window, cx| window.dispatch_action(Box::new(open.clone()), cx))
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
                                        .child(div().min_w_0().truncate().text_sm().child(name.to_string()))
                                        .child(div().min_w_0().truncate().text_sm().text_color(muted).child(folder.to_string())),
                                ),
                        )
                        .context_menu(move |menu, _, _| {
                            let (open, current, copied) = (menu_open.clone(), current.clone(), copied.clone());
                            menu.item(PopupMenuItem::new("Open diff").on_click(move |_, window, cx| {
                                window.dispatch_action(Box::new(open.clone()), cx)
                            }))
                            .item(PopupMenuItem::new("Open current file").on_click(move |_, window, cx| {
                                window.dispatch_action(Box::new(current.clone()), cx)
                            }))
                            .item(PopupMenuItem::new("Copy path").on_click(move |_, _, cx| {
                                cx.write_to_clipboard(ClipboardItem::new_string(copied.clone()))
                            }))
                        })
                        .into_any_element()
                }))
                .into_any_element(),
        };
        div().pb_1().pl_5().child(content).into_any_element()
    }
}
