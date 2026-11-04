//! `SourceControlPane` is the drawer's Source control tab: the selected session's project's branch and changed files
//! from `git/status`, read only while the tab is shown (`SessionRead`, scm#12). The pane owns one write at a time
//! (`GitWrites`) whichever control or dialog asked, and every write carries the source and checkout root of the read it
//! was decided against. Replaces SourceControlPanel.svelte.

use super::actions_menu::{MenuAction, MenuContext, PublishRow, WriteState};
use super::branch_menu::{BranchMenu, BranchPicked};
use super::commit_box::PLACEHOLDER;
use super::commit_timeline::CommitTimeline;
use super::git_action::GitWrites;
use super::git_branches::GitBranchList;
use super::git_failure::{GitFailure, Recovery};
use super::git_status::{GitStatus, Section};
use super::git_target::GitTarget;
use super::pull_requests::pr_section::PrSection;
use super::result_line::PaneResult;
use crate::core::state::session_read::{SessionRead, Shown};
use crate::core::ui::center_panes::CenterPanes;
use gpui_kit::component::dock::{
    BasePanel, DockPlacement, PaneRef, Panel, PanelEvent, PanelHandle, panel_handle, register_panel,
};
use gpui_kit::component::input::{InputEvent, TextareaState};
use gpui_kit::*;
use std::collections::HashSet;

#[path = "source_control_actions.rs"]
mod actions;
#[path = "branch_checkout.rs"]
mod checkout;
#[path = "header.rs"]
mod header;
#[path = "source_control_outcomes.rs"]
mod outcomes;
#[path = "render.rs"]
mod rendering;
#[path = "change_sections.rs"]
mod sections;

/// The pane's name in the saved layout (g0-shape's `SOURCE_CONTROL`).
const SOURCE_CONTROL: &str = "Source control";

/// What the pane itself started, so its answer lands only on what it was decided against.
struct RunningOperation {
    action: &'static str,
    target: GitTarget,
    edit_revision: u64,
}
pub struct SourceControlPane {
    focus: FocusHandle,
    window: AnyWindowHandle,
    read: Entity<SessionRead<GitStatus>>,
    window_active: bool,
    active: bool,
    last_session: Option<String>,
    writes: GitWrites,
    message: Entity<TextareaState>,
    amend: bool,
    edit_revision: u64,
    submitted_revision: Option<u64>,
    action_revision: u64,
    running: Option<RunningOperation>,
    finished_op: Option<String>,
    result: Option<PaneResult>,
    result_answers: u64,
    details_open: bool,
    connecting: Option<Task<()>>,
    branches: Option<GitBranchList>,
    branches_failure: Option<String>,
    branches_target: Option<GitTarget>,
    branches_read: Option<Task<()>>,
    branch_menu: Entity<BranchMenu>,
    branch_open: bool,
    checkout: Option<checkout::PendingCheckout>,
    checkout_task: Option<Task<()>>,
    open_sections: HashSet<Section>,
    hovered_section: Option<Section>,
    hovered_file: Option<(Section, String)>,
    selected_file: Option<(Section, String)>,
    timeline: Option<(String, Entity<CommitTimeline>)>,
    timeline_active: bool,
    seen_head: Option<String>,
    pull_requests: Option<(String, Entity<PrSection>)>,
    scroll: ScrollHandle,
    _heard: Vec<Subscription>,
}

gpui_kit::actions!(source_control, [ShowSourceControl]);

impl SourceControlPane {
    /// Registers the pane under the drawer's Source control name, after the placeholders, since the last registration
    /// wins.
    pub fn register(cx: &mut App) {
        register_panel(cx, SOURCE_CONTROL, |_, window, cx| {
            panel_handle(cx.new(|cx| SourceControlPane::new(window, cx)))
        });
        cx.on_action(|_: &ShowSourceControl, cx| {
            let Some(window) = cx.active_window().or_else(|| cx.windows().into_iter().next()) else {
                return;
            };
            cx.defer(move |cx| {
                _ = window.update(cx, |_, window, cx| Self::show(window, cx));
            });
        });
    }
    /// Brings the tab forward in the drawer, opening the drawer if it's shut.
    fn show(window: &mut Window, cx: &mut App) {
        let Some(dock) = CenterPanes::dock(cx) else {
            return;
        };
        let mut found = None;
        for placement in [
            DockPlacement::Left,
            DockPlacement::Center,
            DockPlacement::Right,
            DockPlacement::Bottom,
        ] {
            if let Some(tree) = dock.read(cx).layout(placement) {
                tree.root().walk(&mut |node| {
                    if let PaneRef::Tabs { panels, .. } = node.kind() {
                        for id in panels {
                            if let Some(panel) = dock.read(cx).panel(*id)
                                && let Some(handle) = PanelHandle::of(panel)
                                && handle.view().downcast::<Self>().is_ok()
                            {
                                found = Some((*id, placement));
                            }
                        }
                    }
                });
            }
        }
        if let Some((id, placement)) = found {
            dock.update(cx, |dock, cx| {
                dock.select_panel(id, window, cx);
                if placement != DockPlacement::Center && !dock.is_dock_open(placement) {
                    dock.toggle_dock(placement, window, cx);
                }
            });
        }
    }

    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let read = cx.new(|cx| {
            let mut read = SessionRead::new("git", "status", cx);
            read.refresh_on_settle(cx);
            read
        });
        let message = cx.new(|cx| {
            TextareaState::new(window, cx)
                .placeholder(PLACEHOLDER)
                .auto_grow(3, 8)
        });
        let writes = GitWrites::new(cx);
        let branch_menu = cx.new(|cx| BranchMenu::new(window, cx));
        let handle = window.window_handle();
        let heard = vec![
            cx.observe_window_activation(window, |pane: &mut Self, window, cx| {
                let active = window.is_window_active();
                let regained = active && !pane.window_active;
                pane.window_active = active;
                if regained && pane.active {
                    pane.read.update(cx, |read, cx| read.refresh(cx));
                }
            }),
            cx.observe(&read, |pane: &mut Self, _, cx| pane.read_changed(cx)),
            cx.observe(&writes.action, |pane: &mut Self, _, cx| pane.action_changed(cx)),
            cx.observe(&writes.operation, |pane: &mut Self, _, cx| pane.operation_changed(cx)),
            cx.subscribe(&message, move |pane: &mut Self, _, event: &InputEvent, cx| {
                match event {
                    InputEvent::Change => {
                        pane.edit_revision += 1;
                        cx.notify();
                    }
                    InputEvent::PressEnter { secondary: true, .. } => {
                        let pane = cx.entity().downgrade();
                        cx.defer(move |cx| {
                            _ = handle.update(cx, |_, window, cx| {
                                pane.update(cx, |pane, cx| pane.commit(window, cx))
                            });
                        });
                    }
                    _ => {}
                }
            }),
            cx.subscribe_in(&branch_menu, window, |pane, _, picked: &BranchPicked, window, cx| {
                pane.branch_picked(picked.clone(), window, cx)
            }),
        ];
        Self {
            focus: cx.focus_handle(),
            window: handle,
            read,
            window_active: window.is_window_active(),
            active: false,
            last_session: None,
            writes,
            message,
            amend: false,
            edit_revision: 0,
            submitted_revision: None,
            action_revision: 0,
            running: None,
            finished_op: None,
            result: None,
            result_answers: 0,
            details_open: false,
            connecting: None,
            branches: None,
            branches_failure: None,
            branches_target: None,
            branches_read: None,
            branch_menu,
            branch_open: false,
            checkout: None,
            checkout_task: None,
            open_sections: HashSet::new(),
            hovered_section: None,
            hovered_file: None,
            selected_file: None,
            timeline: None,
            timeline_active: false,
            seen_head: None,
            pull_requests: None,
            scroll: ScrollHandle::new(),
            _heard: heard,
        }
    }

    /// One write at a time, whichever door it came through.
    fn busy(&self, cx: &App) -> bool {
        self.writes.busy(cx) || self.checkout.is_some()
    }
    fn write_state(&self, cx: &App) -> WriteState {
        if self.writes.operation.read(cx).unconfirmed {
            WriteState::Unconfirmed
        } else if self.busy(cx) {
            WriteState::Running
        } else {
            WriteState::Idle
        }
    }
    /// What the pane is showing was read from here; a write sends it back unchanged.
    fn target(&self, cx: &App) -> Option<GitTarget> {
        let read = self.read.read(cx);
        Some(GitTarget {
            session: read.session()?.to_string(),
            source: read.source().cloned(),
            checkout_root: match read.shown() {
                Shown::Read(status) => status.checkout_root.clone(),
                _ => None,
            },
        })
    }
    fn shown_status(&self, cx: &App) -> Option<GitStatus> {
        match self.read.read(cx).shown() {
            Shown::Read(status) => Some(status.clone()),
            _ => None,
        }
    }
    fn menu_rows(&self, cx: &App) -> (Vec<super::actions_menu::MenuRow>, PublishRow) {
        let read = self.read.read(cx);
        let status = match read.shown() {
            Shown::Read(status) => Some(status),
            _ => None,
        };
        let context = MenuContext {
            has_session: read.session().is_some(),
            reading: read.reading(),
            status,
            remotes: self.branches.as_ref().map(|list| list.remotes.as_slice()),
            write: self.write_state(cx),
        };
        (
            super::actions_menu::rows(&context),
            super::actions_menu::publish_row(&context),
        )
    }
    pub fn toggle_details(&mut self, cx: &mut Context<Self>) {
        self.details_open = !self.details_open;
        cx.notify();
    }
    pub fn recover(&mut self, recovery: Recovery, cx: &mut Context<Self>) {
        match recovery {
            Recovery::Refresh => self.read.update(cx, |read, cx| read.refresh(cx)),
            Recovery::Connect(machine) => self.connect(machine, cx),
            Recovery::ReconcileOperation => {
                self.writes.operation.update(cx, |operation, cx| operation.reconcile(cx))
            }
        }
    }
    fn fail(&mut self, sentence: &str, cx: &mut Context<Self>) {
        self.result = Some(PaneResult::Failed(GitFailure::plain(sentence)));
        self.details_open = false;
        cx.notify();
    }
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
        if let Some((_, section)) = &self.pull_requests {
            section.update(cx, |section, cx| section.set_active(active, cx));
        }
        self.sync_children(cx);
    }
}

impl Panel for SourceControlPane {
    fn title(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        SOURCE_CONTROL
    }
}

#[cfg(test)]
#[path = "source_refresh_tests.rs"]
mod refresh_tests;
#[cfg(test)]
#[path = "source_stale_tests.rs"]
mod stale_tests;
