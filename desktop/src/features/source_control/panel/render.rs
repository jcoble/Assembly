//! The pane's one hierarchy: the header, then either why there's nothing to show or a single scroller holding the commit
//! box, what the last action came to, the three sections, the commits and the pull requests.
use super::SourceControlPane;
use super::outcomes::running_label;
use crate::core::state::session_read::Shown;
use crate::core::state::viewed_project::ViewedProject;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::features::source_control::commit_box::{
    can_commit, commit_hint, commit_label, stage_hint, stageable,
};
use crate::features::source_control::git_status::GitStatus;
use crate::features::source_control::result_line::result_line;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::Textarea;
use gpui_kit::component::scroll::ScrollableElement as _;
use gpui_kit::component::switch::Switch;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

const NOT_A_REPOSITORY: &str = "This folder isn't a git repository yet.";
const AMEND_TIP: &str = "Rewrite the last commit instead of adding one. Never do this to a commit that is already pushed and shared.";

/// What an empty panel shows: one icon, a short title, a sentence or two, and at most one thing to press.
fn empty_state(
    title: &'static str,
    body: &'static str,
    actions: Option<AnyElement>,
    cx: &App,
) -> AnyElement {
    let muted = cx.theme().muted_foreground;
    v_flex()
        .w_full()
        .items_center()
        .justify_center()
        .gap_2()
        .px_4()
        .py_8()
        .text_center()
        .child(
            Icon::new(IconName::GitBranch)
                .size(px(24.))
                .text_color(muted),
        )
        .child(
            div()
                .max_w(px(300.))
                .font_weight(FontWeight::MEDIUM)
                .child(title),
        )
        .child(
            div()
                .max_w(px(340.))
                .text_role(TextRole::Body, cx)
                .text_color(muted)
                .child(body),
        )
        .children(actions)
        .into_any_element()
}
impl Render for SourceControlPane {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let header = self.render_header(cx);
        let body = self.render_body(cx);
        v_flex()
            .track_focus(&self.focus)
            .size_full()
            .text_role(TextRole::Body, cx)
            .child(self.open_project.clone())
            .child(header)
            .child(body)
    }
}
/// The bounds a person can drag the commit box between.
const COMMIT_BOX_HEIGHTS: (Pixels, Pixels) = (px(72.), px(320.));
#[derive(Clone)]
struct CommitBoxDrag;
impl Render for CommitBoxDrag {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        Empty
    }
}
/// Pull requests stay with the session's branch, whichever project the rest of the panel is showing.
const PR_SCOPE: &str = "Pull requests below are for the session folder, not this project.";
impl SourceControlPane {
    fn render_body(&mut self, cx: &mut Context<Self>) -> AnyElement {
        match self.read.read(cx).shown().clone() {
            Shown::NoSession => empty_state(
                "No project selected yet",
                "Pick a session and this panel shows that folder's source control.",
                None,
                cx,
            ),
            Shown::Reading => div()
                .px_3()
                .text_role(TextRole::Body, cx)
                .text_color(cx.theme().muted_foreground)
                .child("Reading the changes…")
                .into_any_element(),
            Shown::Failed(message) => self.failure_body(message, cx),
            Shown::Read(status) => self.repository_body(status, cx),
        }
    }
    /// The kit's textarea with a grip under it, the kit having no drag to resize: it grows with its text until dragged.
    fn commit_box(&self, cx: &mut Context<Self>) -> impl IntoElement + use<> {
        let height = self.commit_height;
        v_flex()
            .id("commit-box")
            .w_full()
            .on_drag_move(
                cx.listener(|pane, event: &DragMoveEvent<CommitBoxDrag>, _, cx| {
                    let (least, most) = COMMIT_BOX_HEIGHTS;
                    let height = event.event.position.y - event.bounds.top();
                    pane.commit_height = Some(height.clamp(least, most));
                    cx.notify();
                }),
            )
            .child(
                Textarea::new(&self.message)
                    .small()
                    .when_some(height, |area, height| area.h(height)),
            )
            .child(
                div()
                    .id("commit-box-grip")
                    .w_full()
                    .h(px(6.))
                    .cursor_row_resize()
                    .on_drag(CommitBoxDrag, |drag, _, _, cx| cx.new(|_| drag.clone())),
            )
    }
    fn try_again(&self, cx: &mut Context<Self>) -> Button {
        Button::new("source-control-retry")
            .ghost()
            .compact()
            .label("Try again")
            .on_click(
                cx.listener(|pane, _, _, cx| pane.read.update(cx, |read, cx| read.refresh(cx))),
            )
    }
    fn failure_body(&mut self, message: String, cx: &mut Context<Self>) -> AnyElement {
        let result = self.result_element(cx);
        if message.starts_with(NOT_A_REPOSITORY) {
            let initializing = self.writes.action.read(cx).pending == Some("init");
            let busy = self.busy(cx) || self.read_only(cx);
            let actions = v_flex()
                .items_center()
                .gap_2()
                .child(
                    div().debug_selector(|| "git-initialize".into()).child(
                        Button::new("git-initialize")
                            .primary()
                            .small()
                            .label(if initializing {
                                "Initializing…"
                            } else {
                                "Initialize repository"
                            })
                            .disabled(busy)
                            .on_click(cx.listener(|pane, _, _, cx| pane.initialize(cx))),
                    ),
                )
                .children(result)
                .into_any_element();
            return empty_state(
                "This folder is not a git work tree",
                "It is a plain folder or a bare repository, so there are no changes to show.",
                Some(actions),
                cx,
            );
        }
        let machine = self.read.read(cx).unreachable_machine().map(String::from);
        let danger = cx.theme().danger;
        v_flex()
            .px_3()
            .gap_2()
            .items_start()
            .map(|view| match machine {
                Some(machine) => {
                    let connecting = self.connecting.is_some();
                    let chosen = machine.clone();
                    view.child(
                        div()
                            .text_role(TextRole::Body, cx)
                            .font_weight(FontWeight::MEDIUM)
                            .child(format!("{machine} isn't connected")),
                    )
                    .child(
                        h_flex()
                            .gap_1()
                            .child(
                                Button::new("source-control-connect")
                                    .primary()
                                    .compact()
                                    .label(if connecting {
                                        "Connecting…"
                                    } else {
                                        "Connect"
                                    })
                                    .disabled(connecting)
                                    .on_click(cx.listener(move |pane, _, _, cx| {
                                        pane.connect(chosen.clone(), cx)
                                    })),
                            )
                            .child(self.try_again(cx)),
                    )
                }
                None => view
                    .child(
                        div()
                            .text_role(TextRole::Body, cx)
                            .text_color(danger)
                            .child(message),
                    )
                    .child(self.try_again(cx)),
            })
            .children(result)
            .into_any_element()
    }
    fn result_element(&self, cx: &mut Context<Self>) -> Option<AnyElement> {
        let result = self.result.clone()?;
        Some(result_line(&result, self.details_open, cx))
    }
    fn repository_body(&mut self, status: GitStatus, cx: &mut Context<Self>) -> AnyElement {
        let busy = self.busy(cx);
        let read_only = self.read_only(cx);
        let change_area = (!read_only).then(|| {
            [
                self.commit_box(cx).into_any_element(),
                self.commit_row(&status, busy, cx).into_any_element(),
            ]
        });
        let running = self.running_line(cx);
        let result = self.result_element(cx);
        let sections = self.render_sections(&status, busy || read_only, cx);
        let timeline = self.timeline.as_ref().map(|(_, timeline)| timeline.clone());
        let pull_requests = self
            .pull_requests
            .as_ref()
            .map(|(_, section)| section.clone());
        let muted = cx.theme().muted_foreground;
        div()
            .relative()
            .flex_1()
            .min_h_0()
            .child(
                div()
                    .id("source-control-body")
                    .size_full()
                    .overflow_y_scroll()
                    .track_scroll(&self.scroll)
                    .child(
                        v_flex()
                            .p_2()
                            .gap_1()
                            .children(change_area.into_iter().flatten())
                            .children(running)
                            .children(result)
                            .children(status.more_line().map(|line| {
                                div()
                                    .text_role(TextRole::Body, cx)
                                    .text_color(muted)
                                    .child(line)
                            }))
                            .children(sections)
                            .children(timeline)
                            .children(
                                (ViewedProject::viewed(cx).is_some() && pull_requests.is_some())
                                    .then(|| {
                                        div()
                                            .debug_selector(|| "git-pr-scope".into())
                                            .text_role(TextRole::Body, cx)
                                            .text_color(muted)
                                            .child(PR_SCOPE)
                                    }),
                            )
                            .children(pull_requests),
                    ),
            )
            .vertical_scrollbar(&self.scroll)
            .into_any_element()
    }
    fn commit_row(&mut self, status: &GitStatus, busy: bool, cx: &mut Context<Self>) -> Div {
        let message = self.message.read(cx).value().to_string();
        let can = can_commit(Some(status), &message, self.amend, busy);
        let commit = Button::new("git-commit")
            .small()
            .label(commit_label(self.amend))
            .rounded(px(999.))
            .flex_1()
            .tooltip(commit_hint(Some(status), &message, self.amend, busy))
            .disabled(!can)
            .on_click(cx.listener(|pane, _, window, cx| pane.commit(window, cx)));
        let commit = if can {
            commit.primary()
        } else {
            commit.secondary()
        };
        h_flex()
            .gap_1()
            .child(
                Switch::new("git-amend")
                    .small()
                    .label("Amend")
                    .checked(self.amend)
                    .tooltip(AMEND_TIP)
                    .on_click(cx.listener(|pane, checked: &bool, _, cx| {
                        pane.amend = *checked;
                        cx.notify();
                    })),
            )
            .child(
                Button::new("git-draft")
                    .ghost()
                    .compact()
                    .label("Draft")
                    .tooltip("Draft a commit message")
                    .disabled(busy)
                    .on_click(cx.listener(|pane, _, _, cx| pane.draft_message(cx))),
            )
            .child(
                Button::new("git-stage-all")
                    .secondary()
                    .small()
                    .label("Stage All")
                    .rounded(px(999.))
                    .flex_1()
                    .tooltip(stage_hint(Some(status), busy))
                    .disabled(busy || stageable(status).is_empty())
                    .on_click(cx.listener(|pane, _, _, cx| pane.stage_all(cx))),
            )
            .child(commit)
    }
    fn running_line(&self, cx: &mut Context<Self>) -> Option<AnyElement> {
        let muted = cx.theme().muted_foreground;
        let operation = self.writes.operation.read(cx);
        if operation.busy {
            let label = operation.action.map_or("Working…", running_label);
            let note = operation.error.clone();
            let operation = self.writes.operation.clone();
            return Some(
                v_flex()
                    .gap_1()
                    .child(
                        h_flex()
                            .gap_2()
                            .child(
                                div()
                                    .text_role(TextRole::Body, cx)
                                    .text_color(muted)
                                    .child(label),
                            )
                            .child(
                                Button::new("git-operation-cancel")
                                    .ghost()
                                    .compact()
                                    .label("Cancel")
                                    .on_click(move |_, _, cx| {
                                        operation.update(cx, |operation, cx| operation.cancel(cx))
                                    }),
                            ),
                    )
                    .children(note.map(|note| {
                        div()
                            .text_role(TextRole::Body, cx)
                            .text_color(muted)
                            .child(note)
                    }))
                    .into_any_element(),
            );
        }
        let label = if self.checkout.is_some() {
            "Changing checkout…"
        } else if self.writes.action.read(cx).pending.is_some() {
            "Working…"
        } else {
            return None;
        };
        Some(
            div()
                .text_role(TextRole::Body, cx)
                .text_color(muted)
                .child(label)
                .into_any_element(),
        )
    }
}
