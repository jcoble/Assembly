//! A status in its colour, and a link that looks like one of its neighbours.
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::ExposedElement as _;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::tag::Tag;
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

/// What a status means, so a feature names a meaning and the theme gives the colour.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Tone {
    #[default]
    Neutral,
    Info,
    Success,
    Warning,
    Danger,
    Accent,
}

impl Tone {
    pub fn color(self, cx: &App) -> Hsla {
        let theme = cx.theme();
        match self {
            Self::Neutral => theme.muted_foreground,
            Self::Info => theme.info,
            Self::Success => theme.success,
            Self::Warning => theme.warning,
            Self::Danger => theme.danger,
            Self::Accent => theme.caret,
        }
    }
}

#[derive(IntoElement)]
pub struct StatusChip {
    label: SharedString,
    tone: Tone,
    /// Its id in the accessibility tree, from `control_ids`; without one the chip draws but isn't in the tree.
    tree_id: Option<SharedString>,
}

impl StatusChip {
    pub fn new(label: impl Into<SharedString>, tone: Tone) -> Self {
        Self {
            label: label.into(),
            tone,
            tree_id: None,
        }
    }

    /// Puts the chip in the accessibility tree as a status reading its label.
    pub fn named(mut self, id: impl Into<SharedString>) -> Self {
        self.tree_id = Some(id.into());
        self
    }
}

impl RenderOnce for StatusChip {
    fn render(self, _: &mut Window, cx: &mut App) -> impl IntoElement {
        let color = self.tone.color(cx);
        let tag = Tag::custom(color.opacity(0.12), color, color.opacity(0.24))
            .small()
            .text_role(TextRole::Meta, cx)
            .child(self.label.clone());
        match self.tree_id {
            Some(id) => h_flex()
                .id(id.clone())
                .exposed(Role::Status, id, self.label)
                .child(tag)
                .into_any_element(),
            None => tag.into_any_element(),
        }
    }
}

/// A count or a few words that want attention: the rail's amber chip with its dot.
#[derive(IntoElement)]
pub struct DotChip {
    label: SharedString,
    tone: Tone,
    /// Its id in the accessibility tree, from `control_ids`; without one the chip draws but isn't in the tree.
    tree_id: Option<SharedString>,
}

impl DotChip {
    pub fn new(label: impl Into<SharedString>, tone: Tone) -> Self {
        Self {
            label: label.into(),
            tone,
            tree_id: None,
        }
    }

    /// Puts the chip in the accessibility tree as a status reading its label.
    pub fn named(mut self, id: impl Into<SharedString>) -> Self {
        self.tree_id = Some(id.into());
        self
    }
}

/// The chip's look, on its root: a plain row, or the same row given an id for the tree.
fn dot_chip_look<E: Styled + ParentElement>(root: E, chip: &DotChip, cx: &App) -> E {
    let color = chip.tone.color(cx);
    root.h(px(18.))
        .px(px(7.))
        .gap(px(4.))
        .rounded_full()
        .bg(color.opacity(0.12))
        .text_role(TextRole::Meta, cx)
        .font_weight(FontWeight::SEMIBOLD)
        .text_color(color)
        .child(div().size(px(6.)).rounded_full().bg(color))
        .child(chip.label.clone())
}

impl RenderOnce for DotChip {
    fn render(self, _: &mut Window, cx: &mut App) -> impl IntoElement {
        match self.tree_id.clone() {
            Some(id) => dot_chip_look(h_flex().id(id.clone()), &self, cx)
                .exposed(Role::Status, id, self.label.clone())
                .into_any_element(),
            None => dot_chip_look(h_flex(), &self, cx).into_any_element(),
        }
    }
}

type OnClick = Box<dyn Fn(&ClickEvent, &mut Window, &mut App)>;

#[derive(IntoElement)]
pub struct LinkChip {
    id: ElementId,
    icon: IconName,
    label: SharedString,
    on_click: Option<OnClick>,
}

impl LinkChip {
    pub fn new(id: impl Into<ElementId>, icon: IconName, label: impl Into<SharedString>) -> Self {
        Self {
            id: id.into(),
            icon,
            label: label.into(),
            on_click: None,
        }
    }

    pub fn on_click(
        mut self,
        on_click: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
    ) -> Self {
        self.on_click = Some(Box::new(on_click));
        self
    }
}

impl RenderOnce for LinkChip {
    fn render(self, _: &mut Window, cx: &mut App) -> impl IntoElement {
        Button::new(self.id)
            .ghost()
            .xsmall()
            .accessibility_label(self.label.clone())
            .icon(self.icon)
            .child(div().text_role(TextRole::Meta, cx).child(self.label))
            .when_some(self.on_click, |button, on_click| {
                button.on_click(move |event, window, cx| {
                    // A chip sits on rows and cards that open on a click.
                    cx.stop_propagation();
                    on_click(event, window, cx);
                })
            })
    }
}

#[cfg(test)]
#[path = "chip_tests.rs"]
mod tests;
