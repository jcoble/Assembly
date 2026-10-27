//! The session pane as a dock panel, and its drawing: the header, the timeline, the plan card, the status line and the
//! composer, top to bottom.

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent};
use gpui_kit::component::{ActiveTheme as _, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::SessionPane;
use crate::features::session::view::plan_card::PlanCard;
use crate::features::session::view::session_action::SessionAction;

impl EventEmitter<PanelEvent> for SessionPane {}

impl Focusable for SessionPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for SessionPane {
    /// The chat pane's name in the saved layout (g0-shape's `CHAT`).
    fn panel_name(&self) -> &'static str {
        "Chat"
    }

    /// A permanent tab, as the layout's other panes.
    fn closable(&self, _cx: &App) -> bool {
        false
    }
}

impl Panel for SessionPane {
    fn title(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        "Chat"
    }
}

impl Render for SessionPane {
    /// The header, the timeline, the plan card, the status (why the last call failed, or "Offline, showing the saved
    /// session") and the composer, top to bottom.
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        if self.reveal.is_some() && !self.frame_asked {
            self.frame_asked = true;
            cx.on_next_frame(window, |pane, window, cx| pane.reveal_frame(window, cx));
        }
        div()
            .size_full()
            .flex()
            .flex_col()
            .child(self.header.clone())
            .child(div().flex_1().min_h_0().child(self.timeline.clone()))
            .when_some(
                self.view
                    .plan
                    .as_ref()
                    .filter(|(_, items)| !items.is_empty()),
                |pane, (_, items)| {
                    pane.child(PlanCard {
                        items: items.clone(),
                        open: self.plan_open,
                        on_action: self.on_action.clone(),
                    })
                },
            )
            .when(!self.status.is_empty(), |pane| {
                let on_action = self.on_action.clone();
                pane.child(
                    h_flex()
                        .px_4()
                        .py_1()
                        .gap_2()
                        .text_sm()
                        .text_color(cx.theme().danger)
                        .child(self.status.clone())
                        .when(self.retry_first.is_some(), |line| {
                            line.child(
                                Button::new("retry-page")
                                    .ghost()
                                    .compact()
                                    .label("Try again")
                                    .on_click(move |_, window, cx| {
                                        on_action(SessionAction::RetryPage, window, cx)
                                    }),
                            )
                        }),
                )
            })
            .child(self.composer.clone())
    }
}
