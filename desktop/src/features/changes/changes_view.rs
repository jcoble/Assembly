//! How the Changes tab looks: the bar, what's being waited on, the stacked diff and the file tree.
use super::{ChangesPane, Target};
use crate::core::intents::open_file::OpenFile;
use crate::core::socket::{error_code::ErrorCode, row_source::ExpectedRowSource};
use crate::core::state::{preferences::Preferences, session_read::Shown};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::control_ids as ids;
use crate::core::ui::access::{Control as _, Named as _};
use crate::core::ui::{
    changed_tree::{TREE_WIDTH, TreeHandler},
    diff_header::{file_heading, part_heading, viewed_button},
    reasoned_item::reasoned_item,
};
use crate::features::changes::working_copy::{Scope, is_working_part};
use crate::features::source_control::actions_menu::READ_ONLY_SCOPE;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonGroup, ButtonVariants as _};
use gpui_kit::component::diff::{Diff, DiffMode};
use gpui_kit::component::input::{Editor, EditorState, Input, InputEvent, InputState};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::spinner::Spinner;
use gpui_kit::component::{
    ActiveTheme as _, Disableable as _, Selectable as _, Sizable as _, h_flex, v_flex,
};
use gpui_kit::*;
use std::rc::Rc;

const COMBINED: &str = "Combined conflict diff — shown exactly as Git returned it.";
/// Tall enough to read a conflict in place while the stack below it stays on screen.
const COMBINED_HEIGHT: Pixels = px(240.);
const BAR_HEIGHT: Pixels = px(44.);

/// What stands in for the stack, in the order the old view asked.
#[derive(Debug, PartialEq)]
pub(super) enum Waiting {
    Busy(&'static str),
    Quiet(&'static str),
    Failed(String),
}
/// Whose line a double-click opens, and why it can't.
#[derive(Clone)]
pub(super) struct LineTarget {
    session: Option<String>,
    source: Option<ExpectedRowSource>,
    pub(super) read_only: bool,
}
fn open_line(
    target: &LineTarget,
    path: String,
    line: Option<u32>,
    window: &mut Window,
    cx: &mut App,
) {
    let Some(session) = target.session.clone() else {
        return;
    };
    window.dispatch_action(
        Box::new(OpenFile {
            session_id: session,
            path,
            expected_source: target.source.clone(),
            line,
            column: None,
            pinned: false,
        }),
        cx,
    );
}
/// "Open in editor" and "Copy path", behind the file's ellipsis. Another checkout is for reading, and says so.
fn file_menu(path: &str, target: LineTarget) -> impl IntoElement + use<> {
    let (opened, copied) = (path.to_string(), path.to_string());
    Button::new(SharedString::from(format!("changes-file-menu:{path}")))
        .named("More file actions")
        .ghost()
        .small()
        .icon(IconName::Ellipsis)
        .tooltip("More file actions")
        .dropdown_menu(move |menu, _, _| {
            let reason = target
                .read_only
                .then(|| READ_ONLY_SCOPE.to_string())
                .or_else(|| {
                    target
                        .session
                        .is_none()
                        .then(|| "Pick a session first.".to_string())
                });
            let (target, opened, copied) = (target.clone(), opened.clone(), copied.clone());
            menu.item(reasoned_item(
                "Open in editor",
                reason,
                move |window, cx| {
                    open_line(&target, opened.clone(), None, window, cx);
                },
            ))
            .item(PopupMenuItem::new("Copy path").on_click(move |_, _, cx| {
                cx.write_to_clipboard(ClipboardItem::new_string(copied.clone()));
            }))
        })
}
impl ChangesPane {
    pub(super) fn waiting(&self, cx: &App) -> Option<Waiting> {
        if let Target::Commit(_) = &self.target {
            if let Some(failure) = &self.commit_failure {
                return Some(Waiting::Failed(failure.clone()));
            }
            let reading = self.commit_read.is_some() || self.commit_waiting;
            return reading.then_some(Waiting::Busy("Reading the changes…"));
        }
        let status = self.status.read(cx);
        let deleted = matches!(status.failure_code(), Some(ErrorCode::Refused { code })
            if code == "checkout_unavailable");
        if deleted && status.checkout().is_none() {
            return Some(Waiting::Quiet("Checkout/Worktree deleted."));
        }
        match status.shown() {
            Shown::Failed(message) => Some(Waiting::Failed(message.clone())),
            Shown::NoSession => Some(Waiting::Quiet(
                "Pick a session with a project folder to see its changes.",
            )),
            Shown::Reading => Some(Waiting::Busy("Reading the working copy…")),
            Shown::Read(_)
                if self.working.answers().is_empty()
                    && (self.working.pending() > 0 || status.reading()) =>
            {
                Some(Waiting::Busy("Reading the changes…"))
            }
            Shown::Read(_) => None,
        }
    }
    pub(super) fn line_target(&self) -> LineTarget {
        match &self.target {
            Target::Commit(pick) => LineTarget {
                session: Some(pick.session.clone()),
                source: pick.expected_source.clone(),
                read_only: pick.checkout.is_some(),
            },
            Target::Stack => LineTarget {
                session: self
                    .selection
                    .as_ref()
                    .map(|selection| selection.session.clone()),
                source: self
                    .selection
                    .as_ref()
                    .and_then(|selection| selection.source.clone()),
                read_only: self
                    .selection
                    .as_ref()
                    .is_some_and(|selection| selection.checkout.is_some()),
            },
        }
    }
    fn scope_label(&self) -> String {
        match (self.scope, &self.base, &self.branch_failure) {
            (Scope::Uncommitted, _, _) => "Uncommitted".into(),
            (Scope::Branch, Some(base), None) => format!("Branch vs {base}"),
            (Scope::Branch, _, _) => "Branch".into(),
        }
    }
    fn scope_menu(&self, cx: &mut Context<Self>) -> impl IntoElement + use<> {
        let (scope, pane) = (self.scope, cx.entity().downgrade());
        Button::new("changes-scope")
            .ghost()
            .small()
            .dropdown_caret(true)
            .label(self.scope_label())
            .tooltip("Which changes to show")
            .dropdown_menu(move |menu, _, _| {
                let (uncommitted, branch) = (pane.clone(), pane.clone());
                menu.item(
                    PopupMenuItem::new("Uncommitted")
                        .checked(scope == Scope::Uncommitted)
                        .on_click(move |_, _, cx| {
                            _ = uncommitted.update(cx, |pane, cx| {
                                pane.set_scope(Scope::Uncommitted, cx);
                            });
                        }),
                )
                .item(
                    PopupMenuItem::new("Branch")
                        .checked(scope == Scope::Branch)
                        .on_click(move |_, _, cx| {
                            _ = branch.update(cx, |pane, cx| pane.set_scope(Scope::Branch, cx));
                        }),
                )
            })
    }
    fn bar(&self, cx: &mut Context<Self>) -> impl IntoElement + use<> {
        let theme = cx.theme();
        let (border, muted, added, removed) =
            (theme.border, theme.muted_foreground, theme.green, theme.red);
        let mono = theme.mono_font_family.clone();
        let stack = self.target == Target::Stack;
        let label = match &self.target {
            Target::Commit(pick) => Some(format!("Commit {}", &pick.sha[..pick.sha.len().min(7)])),
            Target::Stack => None,
        };
        let folder = self
            .selection
            .as_ref()
            .and_then(|selection| selection.checkout.as_ref())
            .map(|checkout| checkout.folder().to_string())
            .filter(|_| stack);
        let pending = if stack { self.working.pending() } else { 0 };
        let refreshing = self.status.read(cx).reading();
        let mode = self.mode;
        h_flex()
            .flex_none()
            .min_h(BAR_HEIGHT)
            .pl_3()
            .pr_2()
            .gap_3()
            .border_b_1()
            .border_color(border)
            .text_role(TextRole::Body, cx)
            .children(label.map(|label| div().flex_none().child(label)))
            .children(stack.then(|| self.scope_menu(cx)))
            .children(folder.map(|folder| {
                div()
                    .min_w_0()
                    .truncate()
                    .text_color(muted)
                    .child(format!("in {folder}"))
            }))
            .child(
                h_flex()
                    .flex_none()
                    .gap_1()
                    .text_role(TextRole::Meta, cx)
                    .font_family(mono)
                    .child(div().text_color(added).child(format!("+{}", self.totals.0)))
                    .child(
                        div()
                            .text_color(removed)
                            .child(format!("-{}", self.totals.1)),
                    ),
            )
            .children((pending > 0).then(|| {
                h_flex()
                    .flex_none()
                    .gap_1()
                    .text_color(muted)
                    .child(Spinner::new().xsmall())
                    .child(format!(
                        "Reading {pending} more {}…",
                        if pending == 1 { "file" } else { "files" }
                    ))
            }))
            .child(div().flex_1())
            .child(
                ButtonGroup::new("changes-mode")
                    .compact()
                    .outline()
                    .small()
                    .child(
                        Button::new("changes-unified")
                            .label("Unified")
                            .selected(mode == DiffMode::Unified),
                    )
                    .child(
                        Button::new("changes-split")
                            .label("Side by side")
                            .selected(mode == DiffMode::Split),
                    )
                    .on_click(cx.listener(|pane, picked: &Vec<usize>, _, cx| {
                        let mode = match picked.first() {
                            Some(0) => DiffMode::Unified,
                            Some(1) => DiffMode::Split,
                            _ => return,
                        };
                        pane.set_mode(mode, cx);
                    })),
            )
            .children(stack.then(|| {
                Button::new("changes-refresh")
                    .control(ids::changes::REFRESH, "Refresh changes")
                    .ghost()
                    .small()
                    .icon(IconName::RefreshCw)
                    .tooltip("Refresh changes")
                    .disabled(refreshing)
                    .on_click(cx.listener(|pane, _, _, cx| pane.refresh(cx)))
            }))
    }
    fn stack(&self, cx: &mut Context<Self>) -> Diff {
        let both = self.both.clone();
        let viewed = self.viewed.clone();
        let (header, lines) = (cx.entity().downgrade(), cx.entity().downgrade());
        let target = self.line_target();
        let line_target = target.clone();
        Diff::new(&self.state)
            .render_header(move |file, _, cx| {
                if is_working_part(file) {
                    return part_heading("WORKING TREE", cx).into_any_element();
                }
                let path = file.path().to_string();
                let (toggle, toggled) = (header.clone(), path.clone());
                let actions = h_flex()
                    .gap_1()
                    .child(viewed_button(
                        &path,
                        viewed.contains(&path),
                        move |_, cx| {
                            _ = toggle
                                .update(cx, |pane, cx| pane.toggle_viewed(toggled.clone(), cx));
                        },
                    ))
                    .child(file_menu(&path, target.clone()));
                let tag = both.contains(&path).then_some("STAGED");
                file_heading(file, tag, actions, cx).into_any_element()
            })
            .on_line_click(move |position, click, window, cx| {
                if click.click_count() != 2 {
                    return;
                }
                if line_target.read_only {
                    _ = lines.update(cx, |pane, cx| {
                        pane.line_notice = Some(READ_ONLY_SCOPE);
                        cx.notify();
                    });
                    return;
                }
                open_line(
                    &line_target,
                    position.path().to_string(),
                    u32::try_from(position.line()).ok(),
                    window,
                    cx,
                );
            })
    }
    fn aside(&self, cx: &mut Context<Self>) -> impl IntoElement + use<> {
        let (picked, folded) = (cx.entity().downgrade(), cx.entity().downgrade());
        let on_pick: TreeHandler = Rc::new(move |path, _, cx| {
            let path = path.to_string();
            _ = picked.update(cx, |pane, cx| pane.pick_in_tree(path, cx));
        });
        let on_fold: TreeHandler = Rc::new(move |folder, _, cx| {
            _ = folded.update(cx, |pane, cx| {
                pane.tree.toggle_folder(folder);
                cx.notify();
            });
        });
        v_flex()
            .w(TREE_WIDTH)
            .flex_none()
            .h_full()
            .min_h_0()
            .border_l_1()
            .border_color(cx.theme().border)
            .children(
                self.filter_input
                    .as_ref()
                    .map(|(input, _)| div().p_2().child(Input::new(input).small())),
            )
            .child(div().flex_1().min_h_0().child(self.tree.list(
                "changes-tree",
                Rc::new(self.viewed.clone()),
                self.active_file.clone(),
                on_pick,
                on_fold,
            )))
    }
    fn sync_filter(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.filter_input.is_some() {
            return;
        }
        let input = cx.new(|cx| InputState::new(window, cx).placeholder("Filter files..."));
        let heard = cx.subscribe(&input, |pane: &mut Self, input, event: &InputEvent, cx| {
            if matches!(event, InputEvent::Change) {
                pane.filter = input.read(cx).value().to_string();
                pane.show_filtered(cx);
            }
        });
        self.filter_input = Some((input, heard));
    }
    /// Each conflict's exact bytes, in a read-only editor made when it first shows.
    fn combined_blocks(
        &mut self,
        font: &SharedString,
        size: Pixels,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Vec<Div> {
        let muted = cx.theme().muted_foreground;
        let filter = self.filter.clone();
        self.combined
            .iter_mut()
            .filter(|view| crate::core::ui::changed_tree::matches_filter(&view.path, &filter))
            .map(|view| {
                let text = view.patch.text.clone();
                let state = view.editor.get_or_insert_with(|| {
                    cx.new(|cx| {
                        EditorState::new(window, cx)
                            .default_value(text)
                            .line_number(false)
                            .soft_wrap(false)
                    })
                });
                v_flex()
                    .flex_none()
                    .h(COMBINED_HEIGHT)
                    .child(
                        div()
                            .px_3()
                            .text_role(TextRole::Meta, cx)
                            .text_color(muted)
                            .child(format!("{} · {} · {COMBINED}", view.path, view.patch.label)),
                    )
                    .child(
                        div()
                            .flex_1()
                            .min_h_0()
                            .font_family(font.clone())
                            .text_size(size)
                            .child(
                                Editor::new(state)
                                    .readonly(true)
                                    .appearance(false)
                                    .bordered(false)
                                    .h_full(),
                            ),
                    )
            })
            .collect()
    }
}
impl Render for ChangesPane {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        self.sync_filter(window, cx);
        let bar = self.bar(cx);
        let pane = v_flex()
            .track_focus(&self.focus)
            .size_full()
            .min_h_0()
            .child(bar);
        let (muted, danger) = (cx.theme().muted_foreground, cx.theme().danger);
        let (note_size, note_height) =
            Preferences::read(cx, |appearance| appearance.ui_text(TextRole::Body));
        let note = |text: String| {
            div()
                .flex_none()
                .px_3()
                .py_1()
                .text_size(note_size)
                .line_height(note_height)
                .child(text)
        };
        match self.waiting(cx) {
            Some(Waiting::Busy(text)) => {
                return pane.child(
                    h_flex()
                        .px_3()
                        .py_2()
                        .gap_1()
                        .text_role(TextRole::Body, cx)
                        .text_color(muted)
                        .child(Spinner::new().xsmall())
                        .child(text),
                );
            }
            Some(Waiting::Quiet(text)) => {
                return pane.child(note(text.into()).py_2().text_color(muted));
            }
            Some(Waiting::Failed(message)) => {
                return pane.child(note(message).py_2().text_color(danger));
            }
            None => {}
        }
        let (font, size, _) = Preferences::editor_text(cx);
        let unreadable: Vec<&str> = self.working.unreadable().map(String::as_str).collect();
        let notices = self
            .branch_failure
            .iter()
            .filter(|_| self.scope == Scope::Branch)
            .map(|failure| note(failure.clone()).text_color(danger))
            .chain((!unreadable.is_empty()).then(|| {
                note(format!("Not shown here: {}", unreadable.join(", "))).text_color(muted)
            }))
            .chain(
                self.line_notice
                    .map(|text| note(text.into()).text_color(muted)),
            )
            .chain(
                self.more_line
                    .iter()
                    .map(|text| note(text.clone()).text_color(muted)),
            )
            .chain(
                self.notes
                    .iter()
                    .filter(|(path, _)| {
                        crate::core::ui::changed_tree::matches_filter(path, &self.filter)
                    })
                    .map(|(path, text)| note(format!("{path} · {text}")).text_color(muted)),
            )
            .collect::<Vec<_>>();
        let combined = self.combined_blocks(&font, size, window, cx);
        let parsed = !self.state.read(cx).files().is_empty();
        let nothing = !parsed && combined.is_empty() && notices.is_empty();
        let empty = nothing.then(|| {
            note(if self.filter.trim().is_empty() {
                "No changes.".into()
            } else {
                "No changed file matches the filter.".into()
            })
            .py_2()
            .text_color(muted)
        });
        let stack = parsed.then(|| {
            div()
                .flex_1()
                .min_h_0()
                .font_family(font.clone())
                .text_size(size)
                .child(self.stack(cx))
        });
        let body = v_flex()
            .flex_1()
            .min_w_0()
            .h_full()
            .min_h_0()
            .children(notices)
            .children(combined)
            .children(empty)
            .children(stack);
        let aside = self.aside(cx);
        pane.child(h_flex().flex_1().min_h_0().child(body).child(aside))
    }
}
