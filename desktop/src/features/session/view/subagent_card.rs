//! `SubagentCard` draws a subagent: its title, description and state ("Failed" when idle with an error, "Stopped"
//! when cancelled), opens its own session on click (the open session intent, lane g0-host), and has a Stop button
//! until it's idle and while it can stop (`send/stopTask` with its session id). Replaces chat/Subagent.svelte.

use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::session_action::{OnAction, SessionAction};
use super::task_card::{card, stop_button};
use crate::features::session::rows::subagent::SubagentPayload;

#[derive(IntoElement)]
pub struct SubagentCard {
    pub subagent: SubagentPayload,
    pub on_action: OnAction,
}

/// "Failed" when it stopped with an error, "Stopped" when cancelled, else its state: "Running", "Waiting for you",
/// "Finished", or the state's own name.
pub fn state_label(subagent: &SubagentPayload) -> String {
    let idle = subagent.state == "idle";
    if idle
        && subagent
            .error
            .as_deref()
            .is_some_and(|error| !error.is_empty())
    {
        return "Failed".to_string();
    }
    if idle && subagent.stop_reason.as_deref() == Some("cancelled") {
        return "Stopped".to_string();
    }
    match subagent.state.as_str() {
        "running" => "Running",
        "requires_action" => "Waiting for you",
        "idle" => "Finished",
        other => other,
    }
    .to_string()
}

impl RenderOnce for SubagentCard {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let subagent = &self.subagent;
        let muted = cx.theme().muted_foreground;
        let running = subagent.state != "idle";
        let (session_id, on_action) = (subagent.session_id.clone(), self.on_action.clone());
        card(cx)
            .id(SharedString::from(format!(
                "subagent:{}",
                subagent.session_id
            )))
            .cursor_pointer()
            .on_click(move |_, window, cx| {
                let session_id = session_id.clone();
                on_action(SessionAction::OpenSession { session_id }, window, cx)
            })
            .child(
                h_flex()
                    .gap_2()
                    .child(
                        v_flex()
                            .flex_1()
                            .min_w_0()
                            .child(
                                div()
                                    .font_weight(FontWeight::MEDIUM)
                                    .truncate()
                                    .child(subagent.title.clone()),
                            )
                            .when_some(subagent.description.clone(), |column, description| {
                                column.child(
                                    div()
                                        .text_xs()
                                        .text_color(muted)
                                        .truncate()
                                        .child(description),
                                )
                            }),
                    )
                    .child(
                        div()
                            .text_xs()
                            .text_color(muted)
                            .child(state_label(subagent)),
                    )
                    .when(running && subagent.can_stop, |line| {
                        let id = format!("subagent:{}:stop", subagent.session_id);
                        line.child(stop_button(
                            id,
                            subagent.session_id.clone(),
                            self.on_action.clone(),
                        ))
                    }),
            )
    }
}

#[cfg(test)]
#[path = "subagent_card_tests.rs"]
mod tests;
