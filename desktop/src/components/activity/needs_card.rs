//! A question, permission or approval on a card. It grows when opened, from a click on it or from any link, and shrinks
//! to its summary line once answered. What it opens into is the caller's.
use std::rc::Rc;

use gpui_kit::assets::IconName;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::chip::{StatusChip, Tone};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::row_drop_down::{GrowingBody, RowDropDowns};
use crate::core::ui::three_line_row::row_height;

/// What a row or card opens into. It runs on every frame, including while closing, so it holds its own data.
pub type CardBody = Rc<dyn Fn(&mut Window, &mut App) -> AnyElement>;

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum NeedsState {
    Open,
    Answered {
        summary: SharedString,
        delivery: Option<SharedString>,
    },
}

#[derive(IntoElement)]
pub struct NeedsCard {
    pub key: SharedString,
    /// What's being asked for, such as Question or Permission.
    pub kind: SharedString,
    pub title: SharedString,
    pub why: SharedString,
    pub next_step: Option<SharedString>,
    pub chips: Vec<AnyElement>,
    pub time: Option<SharedString>,
    pub state: NeedsState,
    pub drops: Entity<RowDropDowns>,
    pub body: CardBody,
}

/// The body keeps the card's padding, so a caller's body is only its content.
fn padded(body: CardBody) -> impl Fn(&mut Window, &mut App) -> AnyElement + 'static {
    move |window, cx| {
        div()
            .px_3()
            .pb_3()
            .child(body(window, cx))
            .into_any_element()
    }
}

impl NeedsCard {
    /// From any link. A card that's already open stays open.
    pub fn open(key: SharedString, drops: &Entity<RowDropDowns>, body: CardBody, cx: &mut App) {
        drops.update(cx, |drops, cx| {
            if !drops.is_open(&key) {
                drops.toggle(key, padded(body), cx);
            }
        });
    }

    /// The card was answered. Another card open in the same list stays open.
    pub fn settle(key: &SharedString, drops: &Entity<RowDropDowns>, cx: &mut App) {
        drops.update(cx, |drops, cx| {
            if drops.is_open(key) {
                drops.shut(cx);
            }
        });
    }
}

impl RenderOnce for NeedsCard {
    fn render(self, _: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, border, radius, hover) = (
            theme.muted_foreground,
            theme.border,
            theme.radius,
            theme.muted,
        );
        let open = self.drops.read(cx).is_open(&self.key);
        let tone = match self.state {
            NeedsState::Open => Tone::Warning,
            NeedsState::Answered { .. } => Tone::Success,
        };
        let top = h_flex()
            .gap_2()
            .child(StatusChip::new(self.kind, tone))
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .text_role(TextRole::Title, cx)
                    .child(self.title),
            )
            .children(self.time.map(|time| {
                div()
                    .text_role(TextRole::Meta, cx)
                    .text_color(muted)
                    .child(time)
            }))
            .child(
                Icon::new(if open {
                    IconName::ChevronDown
                } else {
                    IconName::ChevronRight
                })
                .xsmall()
                .text_color(muted),
            );
        let meta = |text: SharedString, cx: &App| {
            div()
                .text_role(TextRole::Meta, cx)
                .text_color(muted)
                .child(text)
        };
        let details = match self.state {
            NeedsState::Open => v_flex()
                .gap_0p5()
                .child(
                    div()
                        .text_role(TextRole::Body, cx)
                        .text_color(muted)
                        .child(self.why),
                )
                .when_some(self.next_step, |column, next| column.child(meta(next, cx))),
            NeedsState::Answered { summary, delivery } => {
                let selector = self.key.clone();
                v_flex()
                    .debug_selector(move || format!("needs-summary:{selector}"))
                    .gap_0p5()
                    .child(div().text_role(TextRole::Body, cx).child(summary))
                    .when_some(delivery, |column, delivery| {
                        column.child(meta(delivery, cx))
                    })
            }
        };
        let (drops, key, body) = (self.drops.clone(), self.key.clone(), self.body);
        let (header_selector, body_selector) = (self.key.clone(), self.key.clone());
        let header = v_flex()
            .id(SharedString::from(format!("needs:{}", self.key)))
            .debug_selector(move || format!("needs-header:{header_selector}"))
            .p_3()
            .gap_1()
            .cursor_pointer()
            .hover(|header| header.bg(hover))
            .child(top)
            .child(details)
            .when(!self.chips.is_empty(), |header| {
                header.child(h_flex().flex_wrap().gap_1().children(self.chips))
            })
            .on_click(move |_, _, cx| {
                let (key, body) = (key.clone(), body.clone());
                drops.update(cx, |drops, cx| drops.toggle(key, padded(body), cx));
            });
        v_flex()
            .w_full()
            .border_1()
            .border_color(border)
            .rounded(radius)
            .overflow_hidden()
            .child(header)
            .child(
                div()
                    .debug_selector(move || format!("needs-body:{body_selector}"))
                    .child(GrowingBody::new(self.key, self.drops, row_height(cx)).max_rows(None)),
            )
    }
}

#[cfg(test)]
#[path = "needs_card_tests.rs"]
mod tests;
