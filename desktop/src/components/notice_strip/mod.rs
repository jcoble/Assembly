//! A strip of words across the top of a pane, in a tone's theme colour, with at most one action button beside them.
//! The strip is drawn under its id and its button under `<id>-action`.
use crate::activity::Tone;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::component::button::Button;
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

#[cfg(test)]
mod tests;

type OnClick = Box<dyn Fn(&ClickEvent, &mut Window, &mut App)>;

#[derive(IntoElement)]
pub struct NoticeStrip {
    id: SharedString,
    message: SharedString,
    tone: Tone,
    action: Option<(SharedString, OnClick)>,
}

impl NoticeStrip {
    pub fn new(id: impl Into<SharedString>, message: impl Into<SharedString>, tone: Tone) -> Self {
        Self {
            id: id.into(),
            message: message.into(),
            tone,
            action: None,
        }
    }

    /// The one button beside the words, and what a click on it does.
    pub fn action(
        mut self,
        label: impl Into<SharedString>,
        on_click: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
    ) -> Self {
        self.action = Some((label.into(), Box::new(on_click)));
        self
    }
}

impl RenderOnce for NoticeStrip {
    fn render(self, _: &mut Window, cx: &mut App) -> impl IntoElement {
        let color = self.tone.color(cx);
        let theme = cx.theme();
        let (id, action_id) = (self.id.clone(), format!("{}-action", self.id));
        h_flex()
            .debug_selector(move || id.to_string())
            .w_full()
            .px_3()
            .py_1()
            .gap_2()
            .bg(color.opacity(0.08))
            .border_b_1()
            .border_color(theme.border)
            .text_role(TextRole::Body, cx)
            .text_color(color)
            .child(div().flex_1().child(self.message))
            .when_some(self.action, |row, (label, on_click)| {
                let button = Button::new(SharedString::from(action_id.clone()))
                    .label(label)
                    .small()
                    .outline()
                    .on_click(move |event, window, cx| on_click(event, window, cx));
                row.child(
                    div()
                        .debug_selector(move || action_id.clone())
                        .child(button),
                )
            })
    }
}
