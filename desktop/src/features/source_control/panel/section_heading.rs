//! The one section heading in Source control: a muted uppercase name with its count, a chevron when it folds, and the
//! section's actions at the right edge.
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::tag::Tag;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex};
use gpui_kit::*;

/// The name and count, for a heading that doesn't fold as well as one that does.
pub fn heading_label(title: &'static str, count: Option<String>, cx: &App) -> Div {
    h_flex()
        .gap_1()
        .text_sm()
        .font_weight(FontWeight::MEDIUM)
        .text_color(cx.theme().muted_foreground)
        .child(title.to_uppercase())
        .children(count.map(|count| Tag::secondary().xsmall().rounded_full().child(count)))
}
pub fn section_heading(
    id: impl Into<SharedString>,
    title: &'static str,
    count: Option<String>,
    open: bool,
    on_toggle: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
    actions: Vec<AnyElement>,
    cx: &App,
) -> Div {
    h_flex()
        .w_full()
        .min_h(px(24.))
        .gap_1()
        .child(
            Button::new(ElementId::Name(id.into()))
                .ghost()
                .compact()
                .on_click(on_toggle)
                .child(
                    h_flex()
                        .gap_1()
                        .text_color(cx.theme().muted_foreground)
                        .child(
                            Icon::new(if open {
                                IconName::ChevronDown
                            } else {
                                IconName::ChevronRight
                            })
                            .size(px(14.)),
                        )
                        .child(heading_label(title, count, cx)),
                ),
        )
        .child(div().flex_1())
        .children(actions)
}
