//! `WorktreesPane` is the drawer's Worktrees tab: the selected session's project's worktrees from `worktrees/list`,
//! read only while the tab is shown (`SessionRead`, scm#4). Each row grows in place to show its folder and head
//! commit. It changes nothing on disk (scm#8). Replaces WorktreesPanel.svelte and worktreeManagerService.ts. Inspect,
//! Remove and Archive aren't ported yet.

use std::collections::HashMap;

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::input::{Input, InputEvent, InputState};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::worktree::Worktree;
use super::worktree_list::WorktreeList;
use super::worktree_row;
use crate::core::state::session_read::{SessionRead, Shown};
use crate::core::ui::expand_row::ExpandRow;

/// The pane's name in the saved layout (g0-shape's `WORKTREES`).
const WORKTREES: &str = "Worktrees";

pub struct WorktreesPane {
    focus: FocusHandle,
    read: Entity<SessionRead<WorktreeList>>,
    filter: Entity<InputState>,
    /// Each row's grow-in-place, by worktree path, kept across reads so an open row stays open.
    rows: HashMap<String, Entity<ExpandRow>>,
    _heard: Vec<Subscription>,
}

impl WorktreesPane {
    /// Registers the pane under the drawer's Worktrees name, after the placeholders, since the last registration wins.
    pub fn register(cx: &mut App) {
        register_panel(cx, WORKTREES, |_, window, cx| {
            panel_handle(cx.new(|cx| WorktreesPane::new(window, cx)))
        });
    }

    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let read = cx.new(|cx| SessionRead::new("worktrees", "list", cx));
        let filter =
            cx.new(|cx| InputState::new(window, cx).placeholder("Filter by branch or folder"));
        let heard = vec![
            cx.observe(&read, |_, _, cx| cx.notify()),
            cx.subscribe(&filter, |_, _, event: &InputEvent, cx| {
                if let InputEvent::Change = event {
                    cx.notify();
                }
            }),
        ];
        Self {
            focus: cx.focus_handle(),
            read,
            filter,
            rows: HashMap::new(),
            _heard: heard,
        }
    }

    /// The row for `worktree`, made the first time it's drawn and given this read's facts after.
    fn row(&mut self, worktree: &Worktree, cx: &mut Context<Self>) -> Entity<ExpandRow> {
        let (title, body) = (worktree.clone(), worktree.clone());
        let title = move |grown: f32, _: &mut Window, cx: &mut App| {
            worktree_row::title(&title, grown, cx).into_any_element()
        };
        let body =
            move |_: &mut Window, cx: &mut App| worktree_row::body(&body, cx).into_any_element();
        match self.rows.get(&worktree.path) {
            Some(row) => {
                row.update(cx, |row, _| {
                    row.set_title(title);
                    row.set_body(body);
                });
                row.clone()
            }
            None => {
                let row = cx.new(|_| ExpandRow::new(false, title, body));
                self.rows.insert(worktree.path.clone(), row.clone());
                row
            }
        }
    }
}

impl EventEmitter<PanelEvent> for WorktreesPane {}

impl Focusable for WorktreesPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for WorktreesPane {
    fn panel_name(&self) -> &'static str {
        WORKTREES
    }

    /// The drawer's panes are permanent tabs (g0-shape#7).
    fn closable(&self, _cx: &App) -> bool {
        false
    }

    fn set_active(&mut self, active: bool, _window: &mut Window, cx: &mut Context<Self>) {
        self.read.update(cx, |read, cx| read.set_active(active, cx));
    }
}

impl Panel for WorktreesPane {
    fn title(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        WORKTREES
    }
}

impl Render for WorktreesPane {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, danger) = (theme.muted_foreground, theme.danger);
        let line = |text: String| div().px_3().text_sm().text_color(muted).child(text);
        let (shown, reading) = {
            let read = self.read.read(cx);
            (read.shown().clone(), read.reading())
        };
        let refresh =
            cx.listener(|pane, _, _, cx| pane.read.update(cx, |read, cx| read.refresh(cx)));
        let header = h_flex()
            .px_3()
            .py_2()
            .gap_2()
            .justify_between()
            .child(
                div()
                    .text_sm()
                    .font_weight(FontWeight::MEDIUM)
                    .child(WORKTREES),
            )
            .child(
                Button::new("worktrees-refresh")
                    .ghost()
                    .compact()
                    .label(if reading { "Reading…" } else { "Refresh" })
                    .disabled(reading || shown == Shown::NoSession)
                    .on_click(refresh),
            );
        let body = match shown {
            Shown::NoSession => {
                line("Pick a session to see its project's worktrees.".into()).into_any_element()
            }
            Shown::Reading => line("Reading the worktrees…".into()).into_any_element(),
            Shown::Failed(message) => v_flex()
                .px_3()
                .gap_2()
                .items_start()
                .child(div().text_sm().text_color(danger).child(message))
                .child(
                    Button::new("worktrees-retry")
                        .ghost()
                        .compact()
                        .label("Try again")
                        .on_click(cx.listener(|pane, _, _, cx| {
                            pane.read.update(cx, |read, cx| read.refresh(cx))
                        })),
                )
                .into_any_element(),
            Shown::Read(list) => {
                let filter = self.filter.read(cx).value().to_string();
                let rows: Vec<Entity<ExpandRow>> = list
                    .shown(&filter)
                    .into_iter()
                    .map(|worktree| self.row(worktree, cx))
                    .collect();
                self.rows
                    .retain(|path, _| list.worktrees.iter().any(|w| &w.path == path));
                v_flex()
                    .flex_1()
                    .min_h_0()
                    .gap_2()
                    .child(div().px_2().child(Input::new(&self.filter).small()))
                    .child(
                        div()
                            .id("worktrees")
                            .flex_1()
                            .min_h_0()
                            .overflow_y_scroll()
                            .px_1()
                            .children(rows)
                            .when_some(list.hidden_line(&filter), |rows, hidden| {
                                rows.child(line(hidden))
                            }),
                    )
                    .into_any_element()
            }
        };
        v_flex()
            .track_focus(&self.focus)
            .size_full()
            .child(header)
            .child(body)
    }
}
