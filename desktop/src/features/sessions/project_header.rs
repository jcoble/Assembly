//! `ProjectHeader` heads one project in the rail: the label the backend gave it, quiet and small, and a New session
//! button that opens the new-session form with the project's folder and its first session's machine filled in.

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex};
use gpui_kit::*;

/// A project's header line: room for its + button.
pub const PROJECT_HEIGHT: Pixels = px(30.);

type OnNew = Box<dyn Fn(&ClickEvent, &mut Window, &mut App)>;

#[derive(IntoElement)]
pub struct ProjectHeader {
    label: String,
    repo_url: String,
    on_new: OnNew,
}

impl ProjectHeader {
    pub fn new(
        label: String,
        repo_url: String,
        on_new: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
    ) -> Self {
        ProjectHeader {
            label,
            repo_url,
            on_new: Box::new(on_new),
        }
    }
}

impl RenderOnce for ProjectHeader {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        h_flex()
            .w_full()
            .h(PROJECT_HEIGHT)
            .items_end()
            .justify_between()
            .gap_2()
            .pl_3()
            .pr_1()
            .child(
                div()
                    .min_w_0()
                    .pb_1()
                    .text_xs()
                    .font_weight(FontWeight::SEMIBOLD)
                    .text_color(theme.muted_foreground)
                    .truncate()
                    .child(self.label.to_uppercase()),
            )
            .child(
                Button::new(SharedString::from(format!("project:{}", self.repo_url)))
                    .ghost()
                    .xsmall()
                    .icon(IconName::Plus)
                    .tooltip("New session in this project")
                    .on_click(self.on_new),
            )
    }
}
