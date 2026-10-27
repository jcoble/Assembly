//! `StepRow` is one step on a 28 px line: its sign (a spinner while it runs, a check or a cross once done, a still dot
//! for one left unfinished), its title and summary, and how long it ran. The running step's title shimmers. A tool
//! call's line is one, in the timeline and in a running turn's activity card; a workflow run's steps can be drawn with
//! it too. Section 11's StepRow.

use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::shimmer::ShimmerText;
use gpui_kit::component::spinner::Spinner;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::grow::chevron;
use crate::features::session::model::turn_line::worked_for;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StepState {
    Running,
    Done,
    Failed,
    /// Started, but what ran it has ended: a tool call in a stopped turn.
    Unfinished,
}

/// What a click on a step that opens does.
pub type OnClick = Box<dyn Fn(&ClickEvent, &mut Window, &mut App)>;

#[derive(IntoElement)]
pub struct StepRow {
    pub id: SharedString,
    pub state: StepState,
    pub title: SharedString,
    pub summary: Option<SharedString>,
    /// After the summary: a tool call's line counts.
    pub tags: Vec<AnyElement>,
    /// How long it ran, in ms; `None` where times aren't shown.
    pub took_ms: Option<i64>,
    /// How far its chevron has turned, for a step that opens.
    pub chevron: Option<f32>,
    pub on_click: Option<OnClick>,
}

impl RenderOnce for StepRow {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let muted = theme.muted_foreground;
        let sign = match self.state {
            StepState::Running => Spinner::new().xsmall().color(muted).into_any_element(),
            StepState::Done => Icon::new(Glyph::CircleCheck)
                .xsmall()
                .text_color(theme.success)
                .into_any_element(),
            StepState::Failed => Icon::new(Glyph::CircleX)
                .xsmall()
                .text_color(theme.danger)
                .into_any_element(),
            StepState::Unfinished => Icon::new(Glyph::Dot)
                .xsmall()
                .text_color(muted)
                .into_any_element(),
        };
        let title = if self.state == StepState::Running {
            ShimmerText::new(self.title)
                .id(SharedString::from(format!("{}:shimmer", self.id)))
                .into_any_element()
        } else {
            div().child(self.title).into_any_element()
        };
        h_flex()
            .id(self.id)
            .h(px(28.))
            .gap_2()
            .text_sm()
            .text_color(muted)
            .when_some(self.on_click, |line, on_click| {
                line.cursor_pointer().on_click(on_click)
            })
            .children(self.chevron.map(chevron))
            .child(sign)
            .child(
                div()
                    .flex_none()
                    .font_weight(FontWeight::MEDIUM)
                    .text_color(theme.foreground)
                    .child(title),
            )
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .child(self.summary.unwrap_or_default()),
            )
            .children(self.tags)
            .when_some(self.took_ms, |line, ms| {
                line.child(div().flex_none().text_xs().child(worked_for(ms)))
            })
    }
}
