//! `StepRow` is one step on a 22 px line at 13 px, muted: its sign (a spinner while it runs, then its kind's icon, or
//! a check or a cross once done, a still dot for one left unfinished), its title and summary (in the code face), the
//! chevron after them for a step that opens, and how long it ran at the end. A failed step reads in the danger colour.
//! The running step's title shimmers. A tool call's line is one, in the timeline and in a running turn's activity
//! card; a workflow run's steps can be drawn with it too. Section 11's StepRow.

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

#[derive(IntoElement)]
pub struct StepRow {
    pub id: SharedString,
    pub state: StepState,
    /// Shown in place of the done, failed or unfinished mark.
    pub glyph: Option<Glyph>,
    pub title: SharedString,
    pub summary: Option<SharedString>,
    /// After the summary: a tool call's line counts.
    pub tags: Vec<AnyElement>,
    /// How long it ran, in ms; `None` where times aren't shown.
    pub took_ms: Option<i64>,
    /// How far its chevron has turned, for a step that opens.
    pub chevron: Option<f32>,
}

impl RenderOnce for StepRow {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let muted = theme.muted_foreground;
        let failed = self.state == StepState::Failed;
        let mark = |mark: Glyph, color: Hsla| {
            let color = if self.glyph.is_some() && !failed {
                muted
            } else {
                color
            };
            (Icon::new(self.glyph.unwrap_or(mark)).xsmall())
                .text_color(color)
                .into_any_element()
        };
        let sign = match self.state {
            StepState::Running => (Spinner::new().xsmall().color(theme.success)).into_any_element(),
            StepState::Done => mark(Glyph::CircleCheck, theme.success),
            StepState::Failed => mark(Glyph::CircleX, theme.danger),
            StepState::Unfinished => mark(Glyph::Dot, muted),
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
            .h(px(22.))
            .gap_2()
            .text_size(px(13.))
            .text_color(if failed { theme.danger } else { muted })
            .child(sign)
            .child(div().flex_none().child(title))
            .child(
                div()
                    .min_w_0()
                    .truncate()
                    .font_family(theme.mono_font_family.clone())
                    .text_size(px(12.))
                    .child(self.summary.unwrap_or_default()),
            )
            .children(self.tags)
            .children(self.chevron.map(chevron))
            .child(div().flex_1())
            .when_some(self.took_ms, |line, ms| {
                line.child(div().flex_none().text_xs().child(worked_for(ms)))
            })
    }
}
