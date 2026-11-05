//! A card: a sheet lifted off its panel by the foreground at low opacity, with a muted icon, a title and, when there
//! is something to count, a count chip, then whatever it holds. Nothing in it expands.
use crate::activity::{StatusChip, Tone};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::assets::IconName;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

#[derive(IntoElement)]
pub struct SectionCard {
    icon: IconName,
    title: SharedString,
    count: Option<usize>,
    children: Vec<AnyElement>,
}

impl SectionCard {
    pub fn new(icon: IconName, title: impl Into<SharedString>) -> Self {
        Self {
            icon,
            title: title.into(),
            count: None,
            children: Vec::new(),
        }
    }

    /// The chip shows only above zero.
    pub fn count(mut self, count: usize) -> Self {
        self.count = Some(count);
        self
    }
}

impl ParentElement for SectionCard {
    fn extend(&mut self, elements: impl IntoIterator<Item = AnyElement>) {
        self.children.extend(elements);
    }
}

impl RenderOnce for SectionCard {
    fn render(self, _: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, sheet) = (theme.muted_foreground, theme.foreground.opacity(0.08));
        let heading = h_flex()
            .w_full()
            .gap_2()
            .child(Icon::new(self.icon).small().text_color(muted))
            .child(
                div()
                    .min_w_0()
                    .truncate()
                    .text_role(TextRole::Title, cx)
                    .font_weight(FontWeight::SEMIBOLD)
                    .child(self.title.clone()),
            )
            .child(div().flex_1())
            .when_some(self.count.filter(|count| *count > 0), |heading, count| {
                // The selector names the card and the number, so a test can read what the chip shows.
                let selector = format!("card-count-{}-{count}", self.title);
                heading.child(
                    div()
                        .flex_none()
                        .debug_selector(move || selector)
                        .child(StatusChip::new(count.to_string(), Tone::Neutral)),
                )
            });
        v_flex()
            .w_full()
            .rounded(theme.radius)
            .p_3()
            .gap_2()
            .bg(sheet)
            .child(heading)
            .children(self.children)
    }
}
