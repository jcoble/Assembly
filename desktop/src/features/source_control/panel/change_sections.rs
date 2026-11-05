//! Staged, Changes and Untracked: three sections that are always there and start folded, each with its letters and
//! paths. A row's buttons mount only while it's hovered, so a long list holds no idle controls. Replaces
//! ChangedFileRow.svelte and sourceControlFileMenu.ts.
use super::SourceControlPane;
use crate::core::intents::{open_diff::OpenDiff, open_file::OpenFile};
use crate::core::socket::checkout_selector::CheckoutSelector;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::Named as _;
use crate::core::ui::file_icon::file_icon;
use crate::core::ui::reasoned_item::reasoned_item;
use crate::features::source_control::actions_menu::{READ_ONLY_SCOPE, STILL_RUNNING};
use crate::features::source_control::git_labels::split_path;
use crate::features::source_control::git_status::{ChangedFile, GitStatus, Section};
use crate::features::source_control::git_target::GitTarget;
use crate::features::source_control::section_heading::section_heading;
use crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::list::ListItem;
use gpui_kit::component::menu::ContextMenuExt as _;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Icon, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

const NO_ROOT: &str = "This panel is not pointed at a repository folder yet.";
const DELETED: &str = "This file was deleted, so there is nothing on disk to open.";
const ALL_STAGED: &str = "Everything in this file is already staged.";
const NOTHING_STAGED: &str = "Nothing about this file is staged.";

/// Which way the row's first button goes.
#[derive(Clone, Copy, Debug, PartialEq)]
pub(super) enum RowWrite {
    Stage,
    Resolve,
    Unstage,
}
impl RowWrite {
    fn label(self) -> &'static str {
        match self {
            Self::Stage => "Stage",
            Self::Resolve => "Mark resolved",
            Self::Unstage => "Unstage",
        }
    }
}
pub(super) fn row_write(file: &ChangedFile, letter: char) -> RowWrite {
    if letter == '!' {
        RowWrite::Resolve
    } else if file.worktree != "." {
        RowWrite::Stage
    } else {
        RowWrite::Unstage
    }
}
/// Why each of a file's actions can't run; a row with no reason can.
#[derive(Debug, PartialEq)]
pub(super) struct FileReasons {
    pub stage: Option<&'static str>,
    pub unstage: Option<&'static str>,
    pub discard: Option<&'static str>,
    pub open: Option<&'static str>,
    pub copy_path: Option<&'static str>,
}
impl FileReasons {
    /// On another checkout nothing changes and nothing opens for editing; a path can still be copied.
    fn read_only(self) -> Self {
        Self {
            stage: Some(READ_ONLY_SCOPE),
            unstage: Some(READ_ONLY_SCOPE),
            discard: Some(READ_ONLY_SCOPE),
            open: Some(READ_ONLY_SCOPE),
            copy_path: self.copy_path,
        }
    }
}
pub(super) fn file_reasons(file: &ChangedFile, has_root: bool, busy: bool) -> FileReasons {
    let write = |reason: Option<&'static str>| if busy { Some(STILL_RUNNING) } else { reason };
    let deleted = file.worktree == "D" || (file.index == "D" && file.worktree == ".");
    FileReasons {
        stage: write((file.worktree == ".").then_some(ALL_STAGED)),
        unstage: write((file.index == "." || file.index == "?").then_some(NOTHING_STAGED)),
        discard: write(None),
        open: if has_root {
            deleted.then_some(DELETED)
        } else {
            Some(NO_ROOT)
        },
        copy_path: (!has_root).then_some(NO_ROOT),
    }
}
/// The word beside a file both staged and edited again, so the two letters aren't mistaken for one change.
pub(super) fn change_word(file: &ChangedFile) -> Option<String> {
    let both = file.index != "." && file.worktree != "." && file.index != file.worktree;
    both.then(|| format!("{} + {}", file.index, file.worktree))
}
/// What a section's Stage all covers: what's listed, without conflicts, which only a person can resolve.
pub(super) fn stageable_in(status: &GitStatus, section: Section) -> Vec<String> {
    status
        .section(section)
        .into_iter()
        .filter(|(letter, _)| *letter != '!')
        .map(|(_, file)| file.path.clone())
        .collect()
}
fn empty_text(section: Section) -> &'static str {
    match section {
        Section::Staged => "Nothing is staged yet.",
        Section::Changes => "No changes to files git already knows about.",
        Section::Untracked => "No new files.",
    }
}
fn letter_tone(letter: char, cx: &App) -> Hsla {
    match letter {
        'A' | '?' => cx.theme().success,
        'D' | '!' => cx.theme().danger,
        'R' | 'C' => cx.theme().primary,
        _ => cx.theme().warning,
    }
}
fn icon_button(
    id: SharedString,
    icon: IconName,
    tooltip: String,
    disabled: bool,
    on_click: impl Fn(&mut Window, &mut App) + 'static,
) -> Button {
    Button::new(id)
        .named(tooltip.clone())
        .ghost()
        .compact()
        .icon(Icon::new(icon).size(px(14.)))
        .tooltip(tooltip)
        .disabled(disabled)
        .on_click(move |_, window, cx| {
            cx.stop_propagation();
            on_click(window, cx)
        })
}
impl SourceControlPane {
    pub(super) fn render_sections(
        &mut self,
        status: &GitStatus,
        busy: bool,
        cx: &mut Context<Self>,
    ) -> Vec<AnyElement> {
        Section::ALL
            .into_iter()
            .map(|section| self.render_section(status, section, busy, cx))
            .collect()
    }
    fn render_section(
        &mut self,
        status: &GitStatus,
        section: Section,
        busy: bool,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let files = status.section(section);
        let open = self.open_sections.contains(&section);
        let actions = if self.hovered_section == Some(section) {
            self.section_actions(status, section, busy, cx)
        } else {
            Vec::new()
        };
        let toggle = cx.listener(move |pane: &mut Self, _: &ClickEvent, _, cx| {
            if !pane.open_sections.remove(&section) {
                pane.open_sections.insert(section);
            }
            WorkspaceCoordinator::changed(cx);
            cx.notify();
        });
        let heading = section_heading(
            format!("git-section-{}", section.label()),
            section.label(),
            Some(files.len().to_string()),
            open,
            toggle,
            actions,
            cx,
        );
        let hover = cx.listener(move |pane: &mut Self, hovered: &bool, _, cx| {
            if *hovered {
                pane.hovered_section = Some(section);
            } else if pane.hovered_section == Some(section) {
                pane.hovered_section = None;
            }
            cx.notify();
        });
        let target = self.shown_target(cx);
        let root = status.checkout_root.clone();
        v_flex()
            .gap_1()
            .child(
                div()
                    .id(SharedString::from(format!(
                        "git-section-hover-{}",
                        section.label()
                    )))
                    .on_hover(hover)
                    .child(heading),
            )
            .when(open && files.is_empty(), |list| {
                list.child(
                    div()
                        .px_2()
                        .text_role(TextRole::Body, cx)
                        .text_color(cx.theme().muted_foreground)
                        .child(empty_text(section)),
                )
            })
            .when_some(target.filter(|_| open), |list, target| {
                list.children(files.iter().map(|(letter, file)| {
                    self.file_row(section, *letter, file, &target, root.clone(), busy, cx)
                }))
            })
            .into_any_element()
    }
    fn section_actions(
        &self,
        status: &GitStatus,
        section: Section,
        busy: bool,
        cx: &mut Context<Self>,
    ) -> Vec<AnyElement> {
        let pane = cx.entity().downgrade();
        let id = |name: &str| SharedString::from(format!("git-section-{name}-{}", section.label()));
        let mut actions = Vec::new();
        if section == Section::Staged {
            let weak = pane.clone();
            actions.push(
                icon_button(
                    id("unstage"),
                    IconName::Minus,
                    "Unstage all".into(),
                    busy,
                    move |_, cx| {
                        _ = weak.update(cx, |pane, cx| pane.unstage_all(cx));
                    },
                )
                .into_any_element(),
            );
        } else {
            let paths = stageable_in(status, section);
            let weak = pane.clone();
            actions.push(
                icon_button(
                    id("stage"),
                    IconName::Plus,
                    "Stage all".into(),
                    busy || paths.is_empty(),
                    move |_, cx| {
                        _ = weak.update(cx, |pane, cx| pane.stage_paths(paths.clone(), cx));
                    },
                )
                .into_any_element(),
            );
        }
        let files: Vec<ChangedFile> = status
            .section(section)
            .into_iter()
            .filter(|(letter, _)| *letter != '!')
            .map(|(_, file)| file.clone())
            .collect();
        actions.push(
            icon_button(
                id("discard"),
                IconName::Undo2,
                "Discard…".into(),
                busy || files.is_empty(),
                move |window, cx| {
                    _ = pane.update(cx, |pane, cx| pane.ask_discard(files.clone(), window, cx));
                },
            )
            .into_any_element(),
        );
        actions
    }
    #[allow(clippy::too_many_arguments)]
    fn file_row(
        &self,
        section: Section,
        letter: char,
        file: &ChangedFile,
        target: &GitTarget,
        root: Option<String>,
        busy: bool,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let key = (section, file.path.clone());
        let hovered = self.hovered_file.as_ref() == Some(&key);
        let selected = self.selected_file.as_ref() == Some(&key);
        let (name, folder) = split_path(&file.path);
        let muted = cx.theme().muted_foreground;
        let checkout = self.selection(cx);
        let reasons = file_reasons(file, root.is_some(), busy);
        let reasons = if checkout.is_some() {
            reasons.read_only()
        } else {
            reasons
        };
        let id = format!("{}:{}", section.label(), file.path);
        let cluster =
            hovered.then(|| self.row_buttons(letter, file, target, &id, name, busy, &reasons, cx));
        let word = (!hovered).then(|| change_word(file)).flatten();
        let row = h_flex()
            .w_full()
            .gap_2()
            .child(
                div()
                    .w(px(12.))
                    .flex_none()
                    .text_center()
                    .text_role(TextRole::Body, cx)
                    .font_family(cx.theme().mono_font_family.clone())
                    .text_color(letter_tone(letter, cx))
                    .child(letter.to_string()),
            )
            .child(file_icon(format!("icon:{id}"), &file.path, cx))
            .child(
                h_flex()
                    .flex_1()
                    .min_w_0()
                    .gap_1()
                    .child(div().truncate().child(name.to_string()))
                    .when(!folder.is_empty(), |line| {
                        line.child(
                            div()
                                .truncate()
                                .text_role(TextRole::Body, cx)
                                .text_color(muted)
                                .child(folder.to_string()),
                        )
                    }),
            )
            .children(word.map(|word| {
                div()
                    .flex_none()
                    .text_role(TextRole::Body, cx)
                    .text_color(muted)
                    .child(word)
            }))
            .children(cluster);
        let open = OpenDiff {
            session_id: target.session.clone(),
            path: file.path.clone(),
        };
        let tip = SharedString::from(file.label());
        let hover_key = key.clone();
        let menu = self.file_menu(letter, file, target, checkout, root, busy, cx);
        ListItem::new(SharedString::from(format!("git-file:{id}")))
            .selected(selected)
            .px_2()
            .py_0()
            .min_h(px(28.))
            .on_click(cx.listener(move |pane, _, window, cx| {
                pane.selected_file = Some(key.clone());
                window.dispatch_action(Box::new(open.clone()), cx);
                cx.notify();
            }))
            .on_hover(cx.listener(move |pane, hovered: &bool, _, cx| {
                if *hovered {
                    pane.hovered_file = Some(hover_key.clone());
                } else if pane.hovered_file.as_ref() == Some(&hover_key) {
                    pane.hovered_file = None;
                }
                cx.notify();
            }))
            .tooltip(move |window, cx| Tooltip::new(tip.clone()).build(window, cx))
            .child(row)
            .context_menu(menu)
            .into_any_element()
    }
    #[allow(clippy::too_many_arguments)]
    fn row_buttons(
        &self,
        letter: char,
        file: &ChangedFile,
        target: &GitTarget,
        id: &str,
        name: &str,
        busy: bool,
        reasons: &FileReasons,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let pane = cx.entity().downgrade();
        let button = |what: &str| SharedString::from(format!("git-file-{what}:{id}"));
        let tip = |label: &str, reason: Option<&'static str>| match reason {
            Some(reason) => reason.to_string(),
            None => format!("{label} — {name}"),
        };
        let path = file.path.clone();
        let write = row_write(file, letter);
        let reason = match write {
            RowWrite::Unstage => reasons.unstage,
            RowWrite::Stage | RowWrite::Resolve => reasons.stage,
        };
        let weak = pane.clone();
        let first = icon_button(
            button("write"),
            if write == RowWrite::Unstage {
                IconName::Minus
            } else {
                IconName::Plus
            },
            tip(write.label(), reason),
            reason.is_some(),
            move |_, cx| {
                _ = weak.update(cx, |pane, cx| match write {
                    RowWrite::Unstage => pane.unstage_paths(vec![path.clone()], cx),
                    RowWrite::Stage | RowWrite::Resolve => pane.stage_paths(vec![path.clone()], cx),
                });
            },
        );
        let discard = (letter != '!').then(|| {
            let weak = pane.clone();
            let file = file.clone();
            icon_button(
                button("discard"),
                IconName::Undo2,
                tip("Discard changes", reasons.discard),
                busy,
                move |window, cx| {
                    _ = weak.update(cx, |pane, cx| {
                        pane.ask_discard(vec![file.clone()], window, cx)
                    });
                },
            )
        });
        let open_file = OpenFile {
            session_id: target.session.clone(),
            path: file.path.clone(),
            expected_source: target.source.clone(),
            line: None,
            column: None,
            pinned: false,
            outside: None,
        };
        let open = icon_button(
            button("open"),
            IconName::ExternalLink,
            tip("Open in editor", reasons.open),
            reasons.open.is_some(),
            move |window, cx| window.dispatch_action(Box::new(open_file.clone()), cx),
        );
        h_flex()
            .flex_none()
            .gap_0p5()
            .child(first)
            .children(discard)
            .child(open)
            .into_any_element()
    }
    #[allow(clippy::too_many_arguments)]
    fn file_menu(
        &self,
        letter: char,
        file: &ChangedFile,
        target: &GitTarget,
        checkout: Option<CheckoutSelector>,
        root: Option<String>,
        busy: bool,
        cx: &mut Context<Self>,
    ) -> impl Fn(
        gpui_kit::component::menu::PopupMenu,
        &mut Window,
        &mut Context<gpui_kit::component::menu::PopupMenu>,
    ) -> gpui_kit::component::menu::PopupMenu
    + 'static
    + use<> {
        let pane = cx.entity().downgrade();
        let file = file.clone();
        let target = target.clone();
        move |menu, _, _| {
            let reasons = file_reasons(&file, root.is_some(), busy);
            let view_reason = reasons.open;
            let reasons = if checkout.is_some() {
                reasons.read_only()
            } else {
                reasons
            };
            let reason = |reason: Option<&'static str>| reason.map(String::from);
            let view = OpenDiff {
                session_id: target.session.clone(),
                path: file.path.clone(),
            };
            let open = OpenFile {
                session_id: target.session.clone(),
                path: file.path.clone(),
                expected_source: target.source.clone(),
                line: None,
                column: None,
                pinned: false,
                outside: None,
            };
            let (reveal_pane, reveal_path) = (pane.clone(), file.path.clone());
            let (stage_pane, stage_path) = (pane.clone(), file.path.clone());
            let (unstage_pane, unstage_path) = (pane.clone(), file.path.clone());
            let (discard_pane, discard_file) = (pane.clone(), file.clone());
            let relative = file.path.clone();
            let absolute = root
                .as_ref()
                .map(|root| format!("{}/{}", root.trim_end_matches('/'), file.path));
            menu.min_w(px(220.))
                .max_w(px(220.))
                .item(reasoned_item(
                    "View changes",
                    reason(view_reason),
                    move |window, cx| window.dispatch_action(Box::new(view.clone()), cx),
                ))
                .item(reasoned_item(
                    "Stage this file",
                    reason(reasons.stage),
                    move |_, cx| {
                        _ = stage_pane.update(cx, |pane, cx| {
                            pane.stage_paths(vec![stage_path.clone()], cx)
                        });
                    },
                ))
                .item(reasoned_item(
                    "Unstage this file",
                    reason(reasons.unstage),
                    move |_, cx| {
                        _ = unstage_pane.update(cx, |pane, cx| {
                            pane.unstage_paths(vec![unstage_path.clone()], cx)
                        });
                    },
                ))
                .when(letter != '!', |menu| {
                    menu.item(reasoned_item(
                        "Discard changes…",
                        reason(reasons.discard),
                        move |window, cx| {
                            _ = discard_pane.update(cx, |pane, cx| {
                                pane.ask_discard(vec![discard_file.clone()], window, cx)
                            });
                        },
                    ))
                })
                .item(reasoned_item(
                    "Copy path",
                    reason(reasons.copy_path),
                    move |_, cx| {
                        if let Some(path) = &absolute {
                            cx.write_to_clipboard(ClipboardItem::new_string(path.clone()));
                        }
                    },
                ))
                .item(reasoned_item("Copy relative path", None, move |_, cx| {
                    cx.write_to_clipboard(ClipboardItem::new_string(relative.clone()));
                }))
                .item(reasoned_item(
                    "Open in editor",
                    reason(reasons.open),
                    move |window, cx| window.dispatch_action(Box::new(open.clone()), cx),
                ))
                .item(reasoned_item(
                    "Reveal in Finder",
                    reason(reasons.open),
                    move |_, cx| {
                        _ = reveal_pane
                            .update(cx, |pane, cx| pane.reveal_file(reveal_path.clone(), cx));
                    },
                ))
        }
    }
}
#[cfg(test)]
#[path = "change_sections_tests.rs"]
mod tests;
