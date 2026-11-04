//! A titled group of activity: its dot, title and count, an optional action, and what's in it.
use super::chip::Tone;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

#[derive(IntoElement)]
pub struct ActivitySection {
    id: ElementId,
    title: SharedString,
    tone: Tone,
    subtitle: Option<SharedString>,
    count: Option<usize>,
    action: Option<AnyElement>,
    children: Vec<AnyElement>,
}

impl ActivitySection {
    pub fn new(id: impl Into<ElementId>, title: impl Into<SharedString>) -> Self {
        Self {
            id: id.into(),
            title: title.into(),
            tone: Tone::Neutral,
            subtitle: None,
            count: None,
            action: None,
            children: Vec::new(),
        }
    }

    pub fn tone(mut self, tone: Tone) -> Self {
        self.tone = tone;
        self
    }

    pub fn subtitle(mut self, subtitle: impl Into<SharedString>) -> Self {
        self.subtitle = Some(subtitle.into());
        self
    }

    pub fn count(mut self, count: usize) -> Self {
        self.count = Some(count);
        self
    }

    pub fn action(mut self, action: impl IntoElement) -> Self {
        self.action = Some(action.into_any_element());
        self
    }
}

impl ParentElement for ActivitySection {
    fn extend(&mut self, elements: impl IntoIterator<Item = AnyElement>) {
        self.children.extend(elements);
    }
}

impl RenderOnce for ActivitySection {
    fn render(self, _: &mut Window, cx: &mut App) -> impl IntoElement {
        let muted = cx.theme().muted_foreground;
        let header = h_flex()
            .w_full()
            .gap_2()
            .child(div().size_2().flex_none().rounded_full().bg(self.tone.color(cx)))
            .child(div().min_w_0().truncate().text_role(TextRole::Title, cx).child(self.title))
            .when_some(self.count, |header, count| {
                header.child(div().text_role(TextRole::Meta, cx).text_color(muted).child(count.to_string()))
            })
            .child(div().flex_1())
            .children(self.action);
        v_flex()
            .id(self.id)
            .w_full()
            .gap_2()
            .child(header)
            .when_some(self.subtitle, |section, subtitle| {
                section.child(div().text_role(TextRole::Meta, cx).text_color(muted).child(subtitle))
            })
            .children(self.children)
    }
}
