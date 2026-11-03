//! `SourceControlPane` is the drawer's Source control tab: the selected session's project's branch and changed files
//! from `git/status`, read only while the tab is shown (`SessionRead`, scm#12). The files are in Staged, Changes and
//! Untracked, each with its letter (scm#10). Picking a file opens its diff, and Branch diff opens the branch's diff
//! against its base (scm#13, scm#18). Replaces SourceControlPanel.svelte's status lists. Staging, commits and
//! branches use the shared Git producers.

use super::git_action::GitAction;
use super::sync_controls::{SyncCompleted, SyncControls};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::checkbox::Checkbox;
use gpui_kit::component::input::{Input, InputEvent, InputState};
use serde_json::json;
#[path = "source_control_actions.rs"]
mod actions;
use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::commit_timeline::CommitTimeline;
use super::git_status::{GitStatus, Section};
use super::pull_requests::pr_section::PrSection;
use crate::core::intents::open_branch_diff::OpenBranchDiff;
use crate::core::intents::open_diff::OpenDiff;
use crate::core::intents::open_git_history::OpenGitHistory;
use crate::core::state::session_read::{SessionRead, Shown};

/// The pane's name in the saved layout (g0-shape's `SOURCE_CONTROL`).
const SOURCE_CONTROL: &str = "Source control";

pub struct SourceControlPane {
    focus: FocusHandle,
    read: Entity<SessionRead<GitStatus>>,
    _heard: Subscription,
    _activation: Subscription,
    window_active: bool,
    active: bool,
    pull_requests: Option<(String, Entity<PrSection>)>,
    sync: Option<(String, Entity<SyncControls>, Subscription)>,
    history_open: bool,
    history: Option<(String, Entity<CommitTimeline>)>,
    message: Entity<InputState>,
    amend: bool,
    action: Entity<GitAction>,
    action_revision: u64,
    edit_revision: u64,
    submitted_revision: Option<u64>,
    _action_heard: Subscription,
    _message_heard: Subscription,
}

impl SourceControlPane {
    /// Registers the pane under the drawer's Source control name, after the placeholders, since the last registration
    /// wins.
    pub fn register(cx: &mut App) {
        register_panel(cx, SOURCE_CONTROL, |_, window, cx| {
            panel_handle(cx.new(|cx| SourceControlPane::new(window, cx)))
        });
    }

    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let read = cx.new(|cx| {
            let mut read = SessionRead::new("git", "status", cx);
            read.refresh_on_settle(cx);
            read
        });
        let activation = cx.observe_window_activation(window, |pane: &mut Self, window, cx| {
            let active = window.is_window_active();
            let regained = active && !pane.window_active;
            pane.window_active = active;
            if regained && pane.active {
                pane.read.update(cx, |read, cx| read.refresh(cx));
            }
        });
        let heard = cx.observe(&read, |pane: &mut Self, _, cx| {
            pane.sync_history(cx);
            cx.notify();
        });
        let message =
            cx.new(|cx| InputState::new(window, cx).placeholder("Message (⌘Enter to commit)"));
        let action = cx.new(|_| GitAction::default());
        let handle = window.window_handle();
        let action_heard = cx.observe(&action, move |pane: &mut Self, _, cx| {
            let action = pane.action.read(cx);
            let completed = action.completed;
            let succeeded = action.succeeded;
            let session = action.session.clone();
            if completed > pane.action_revision {
                pane.action_revision = completed;
                if session.as_deref() == pane.read.read(cx).session() {
                    pane.read.update(cx, |read, cx| read.refresh(cx));
                    if let Some((_, timeline)) = &pane.history {
                        timeline.update(cx, |timeline, cx| timeline.refresh(cx));
                    }
                    if succeeded && pane.submitted_revision == Some(pane.edit_revision) {
                        let input = pane.message.clone();
                        cx.defer(move |cx| {
                            _ = handle.update(cx, |_, window, cx| {
                                input.update(cx, |input, cx| input.set_value("", window, cx))
                            });
                        });
                    }
                }
                pane.submitted_revision = None;
            }
            cx.notify();
        });
        let message_window = window.window_handle();
        let message_heard = cx.subscribe(
            &message,
            move |pane: &mut Self, _, event: &InputEvent, cx| {
                if matches!(event, InputEvent::Change) {
                    pane.edit_revision += 1;
                    cx.notify();
                }
                if matches!(
                    event,
                    InputEvent::PressEnter {
                        secondary: true,
                        ..
                    }
                ) {
                    let pane = cx.entity().downgrade();
                    cx.defer(move |cx| {
                        _ = message_window.update(cx, |_, window, cx| {
                            pane.update(cx, |pane, cx| pane.commit(window, cx))
                        });
                    });
                }
            },
        );
        Self {
            focus: cx.focus_handle(),
            read,
            _heard: heard,
            _activation: activation,
            window_active: window.is_window_active(),
            active: false,
            pull_requests: None,
            sync: None,
            history_open: false,
            history: None,
            message,
            amend: false,
            action,
            action_revision: 0,
            edit_revision: 0,
            submitted_revision: None,
            _action_heard: action_heard,
            _message_heard: message_heard,
        }
    }

    fn sync_history(&mut self, cx: &mut Context<Self>) {
        let session = self.read.read(cx).session().map(String::from);
        if self.sync.as_ref().map(|(session, _, _)| session) != session.as_ref() {
            self.sync = session.as_ref().map(|session| {
                let controls = cx.new(|cx| SyncControls::new(session.clone(), self.active, cx));
                let heard = cx.subscribe(&controls, |pane, _, _: &SyncCompleted, cx| {
                    pane.read.update(cx, |read, cx| read.refresh(cx))
                });
                (session.clone(), controls, heard)
            });
        }
        if let Some((_, controls, _)) = &self.sync {
            let status = match self.read.read(cx).shown() {
                Shown::Read(status) => Some(status.clone()),
                _ => None,
            };
            controls.update(cx, |controls, cx| controls.set_status(status, cx));
        }
        if self.pull_requests.as_ref().map(|(session, _)| session) != session.as_ref() {
            self.pull_requests = session.as_ref().map(|session| {
                (
                    session.clone(),
                    cx.new(|cx| PrSection::new(session.clone(), self.active, cx)),
                )
            });
        }
        if self.history.as_ref().map(|(session, _)| session) != session.as_ref() {
            self.history = None;
            if self.history_open {
                if let Some(session) = session {
                    let timeline =
                        cx.new(|cx| CommitTimeline::new(session.clone(), None, self.read.read(cx).source().cloned(), self.active, cx));
                    self.history = Some((session, timeline));
                }
            }
        }
        if let Some((_, timeline)) = &self.history {
            let source = self.read.read(cx).source().cloned();
            timeline.update(cx, |timeline, cx| timeline.set_target(None, source, cx));
        }
    }

    fn toggle_history(&mut self, cx: &mut Context<Self>) {
        self.history_open = !self.history_open;
        if self.history.is_none() {
            self.sync_history(cx);
        } else if let Some((_, timeline)) = &self.history {
            timeline.update(cx, |timeline, cx| {
                timeline.set_active(self.active && self.history_open, cx)
            });
        }
        cx.notify();
    }
}

/// The lists, each under its name and count; an empty one is left out. Picking a file opens its diff (scm#13).
fn lists(
    status: &GitStatus,
    session: &str,
    expected_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    pane: WeakEntity<SourceControlPane>,
    busy: bool,
    cx: &App,
) -> Div {
    let theme = cx.theme();
    let muted = theme.muted_foreground;
    v_flex()
        .gap_2()
        .children(Section::ALL.into_iter().filter_map(|section| {
            let files = status.section(section);
            if files.is_empty() {
                return None;
            }
            let heading = h_flex()
                .px_2()
                .gap_2()
                .text_xs()
                .font_weight(FontWeight::MEDIUM)
                .text_color(muted)
                .child(section.label())
                .child(files.len().to_string());
            let rows = files.into_iter().map(|(letter, file)| {
                let color = match letter {
                    'A' | 'C' | 'U' => theme.success,
                    'D' | '!' => theme.danger,
                    _ => theme.warning,
                };
                let open = OpenDiff {
                    expected_source: expected_source.clone(),
                    session_id: session.to_string(),
                    path: file.path.clone(),
                    original_path: file.original_path.clone(),
                };
                let stage_target = pane.clone();
                let discard_target = pane.clone();
                let staged = section == Section::Staged;
                let method = if staged { "unstage" } else { "stage" };
                let args = json!({"session":session,"paths":[file.path.clone()]});
                let discarded = file.clone();
                let action = Button::new(SharedString::from(format!(
                    "{method}:{}:{}",
                    section.label(),
                    file.path
                )))
                .ghost()
                .compact()
                .label(if staged {
                    "Unstage"
                } else if letter == '!' {
                    "Mark resolved"
                } else {
                    "Stage"
                })
                .disabled(busy)
                .on_click(move |_, _, cx| {
                    cx.stop_propagation();
                    _ = stage_target
                        .update(cx, |pane, cx| pane.start_write(method, args.clone(), cx));
                });
                h_flex()
                    .id(SharedString::from(format!(
                        "{}:{}",
                        section.label(),
                        file.path
                    )))
                    .h(px(24.))
                    .px_2()
                    .gap_2()
                    .text_sm()
                    .rounded_sm()
                    .cursor_pointer()
                    .hover(|row| row.bg(theme.list_hover))
                    .on_click(move |_, window, cx| {
                        window.dispatch_action(Box::new(open.clone()), cx)
                    })
                    .child(
                        div()
                            .flex_none()
                            .w(px(12.))
                            .text_color(color)
                            .child(letter.to_string()),
                    )
                    .child(div().flex_1().min_w_0().truncate().child(file.label()))
                    .child(action)
                    .when(!staged && letter != '!', |row| {
                        row.child(
                            Button::new(SharedString::from(format!("discard:{}", file.path)))
                                .ghost()
                                .compact()
                                .label("Discard")
                                .disabled(busy)
                                .on_click(move |_, window, cx| {
                                    cx.stop_propagation();
                                    _ = discard_target.update(cx, |pane, cx| {
                                        pane.ask_discard(
                                            vec![discarded.clone()],
                                            None,
                                            0,
                                            window,
                                            cx,
                                        )
                                    });
                                }),
                        )
                    })
            });
            Some(v_flex().child(heading).children(rows))
        }))
}

impl EventEmitter<PanelEvent> for SourceControlPane {}

impl Focusable for SourceControlPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for SourceControlPane {
    fn panel_name(&self) -> &'static str {
        SOURCE_CONTROL
    }

    /// The drawer's panes are permanent tabs (g0-shape#7).
    fn closable(&self, _cx: &App) -> bool {
        false
    }

    fn set_active(&mut self, active: bool, _window: &mut Window, cx: &mut Context<Self>) {
        self.active = active;
        self.read.update(cx, |read, cx| read.set_active(active, cx));
        if let Some((_, controls, _)) = &self.sync {
            controls.update(cx, |controls, cx| controls.set_active(active, cx));
        }
        if let Some((_, section)) = &self.pull_requests {
            section.update(cx, |section, cx| section.set_active(active, cx));
        }
        if let Some((_, timeline)) = &self.history {
            timeline.update(cx, |timeline, cx| {
                timeline.set_active(active && self.history_open, cx)
            });
        }
    }
}

impl Panel for SourceControlPane {
    fn title(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        SOURCE_CONTROL
    }
}

#[path = "source_control_pane/render.rs"]
mod rendering;

#[cfg(test)]
#[path = "source_refresh_tests.rs"]
mod refresh_tests;
