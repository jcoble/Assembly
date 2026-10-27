//! `TaskCard` draws background work: its name, state and a Stop button while it runs and can stop (`send/stopTask`).
//! Its state shows as a still mark; nothing animates. Replaces chat/Task.svelte.

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::session_action::{OnAction, SessionAction};
use crate::features::session::rows::task::TaskPayload;

#[derive(IntoElement)]
pub struct TaskCard {
    pub task_id: String,
    pub task: TaskPayload,
    pub on_action: OnAction,
}

/// A card's frame, shared by the task, subagent and ask cards.
pub fn card(cx: &App) -> Div {
    let theme = cx.theme();
    div()
        .w_full()
        .px_3()
        .py_2()
        .rounded(theme.radius)
        .border_1()
        .border_color(theme.border)
        .text_sm()
}

/// A Stop button that sends `send/stopTask` with `task`.
pub fn stop_button(id: String, task: String, on_action: OnAction) -> Button {
    Button::new(SharedString::from(id))
        .ghost()
        .compact()
        .label("Stop")
        .on_click(move |_, window, cx| {
            let task = task.clone();
            on_action(SessionAction::StopTask { task }, window, cx)
        })
}

impl RenderOnce for TaskCard {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let running = self.task.state == "running";
        let mark = match self.task.state.as_str() {
            "running" => "●",
            "completed" => "✓",
            "failed" => "✗",
            _ => "○",
        };
        card(cx).child(
            h_flex()
                .gap_2()
                .child(mark)
                .child(
                    div()
                        .flex_1()
                        .min_w_0()
                        .truncate()
                        .child(self.task.name.clone()),
                )
                .child(
                    div()
                        .text_xs()
                        .text_color(cx.theme().muted_foreground)
                        .child(self.task.state.clone()),
                )
                .when(running && self.task.can_stop, |line| {
                    let id = format!("task:{}:stop", self.task_id);
                    line.child(stop_button(
                        id,
                        self.task_id.clone(),
                        self.on_action.clone(),
                    ))
                }),
        )
    }
}
