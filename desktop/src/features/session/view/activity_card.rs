//! `ActivityCard` is a running turn's tool calls as one card at the foot of the timeline: a Working pill, its steps
//! (each a `StepRow`, the running one shimmering with a live time) and "3 steps · 0:41" under them, in place of the
//! "Working for" line. When the turn ends its work folds under "Worked for…" like any finished turn. It takes its
//! steps drawn, so a workflow run's steps can fill it too. The reui agent-activity look.

use gpui_kit::component::spinner::Spinner;
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex, v_flex};
use gpui_kit::*;

#[derive(IntoElement)]
pub struct ActivityCard {
    pub steps: Vec<AnyElement>,
    /// "3 steps · 0:41".
    pub footer: String,
}

impl RenderOnce for ActivityCard {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let pill = h_flex()
            .gap_1()
            .px_2()
            .py(px(2.))
            .rounded(theme.radius_full())
            .bg(theme.secondary)
            .text_xs()
            .text_color(theme.foreground)
            .child(Spinner::new().xsmall().color(theme.muted_foreground))
            .child("Working");
        v_flex()
            .mx_4()
            .px_3()
            .py_2()
            .gap_1()
            .rounded(px(12.))
            .bg(theme.group_box)
            .child(h_flex().child(pill))
            .children(self.steps)
            .child(
                div()
                    .text_xs()
                    .text_color(theme.muted_foreground)
                    .child(self.footer),
            )
    }
}
