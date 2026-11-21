//! A pill on the window's glass: the top bar's controls (Home, Board, the terminal and drawer toggles, "N need you"),
//! drawn as the rule sheet's "Window chrome and top bar" draws them. Text and icons use the on-glass colours only,
//! a pressed or open pill gets the on-glass selected fill, and an outlined one the chip outline.
//!
//! The kit's `Button` sets its own hover and pressed colours after the caller's, which on glass come out as opaque
//! grey blocks, so the top bar uses this instead. It still works as a popover or menu trigger, since it is
//! `Selectable` and the menu trait is implemented for it below. Callers: top_bar.rs, terminal_toggle.rs,
//! drawer_buttons.rs.

use gpui_kit::assets::IconName;
use gpui_kit::component::menu::DropdownMenu;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{Icon, Selectable, StyledExt as _};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::core::theme::look::Look;
use crate::core::theme::metrics::{BUTTON_HEIGHT, CORNER_PILL};

/// An icon inside a pill: the sheet's icon-button size.
pub const PILL_ICON: Pixels = px(16.);

#[derive(IntoElement)]
pub struct GlassPill {
    /// The pill's element, which carries the caller's id, handlers and any size or padding it sets.
    base: Stateful<Div>,
    /// Pressed, or holding its menu or popover open.
    selected: bool,
    /// Drawn with the chip outline, as Board is.
    outlined: bool,
    tooltip: Option<SharedString>,
}

impl GlassPill {
    /// A pill with words in it; the caller sets its padding.
    pub fn new(id: impl Into<ElementId>) -> Self {
        Self {
            base: div().id(id.into()),
            selected: false,
            outlined: false,
            tooltip: None,
        }
    }

    /// A round 32px pill holding one icon.
    pub fn icon(id: impl Into<ElementId>, icon: IconName) -> Self {
        let pill = Self::new(id);
        pill.size(BUTTON_HEIGHT)
            .justify_center()
            .child(Icon::new(icon).size(PILL_ICON))
    }

    pub fn outlined(mut self) -> Self {
        self.outlined = true;
        self
    }

    /// The words shown when the pointer rests on it.
    pub fn tooltip(mut self, words: impl Into<SharedString>) -> Self {
        self.tooltip = Some(words.into());
        self
    }
}

impl Styled for GlassPill {
    fn style(&mut self) -> &mut StyleRefinement {
        self.base.style()
    }
}

impl InteractiveElement for GlassPill {
    fn interactivity(&mut self) -> &mut Interactivity {
        self.base.interactivity()
    }
}

impl StatefulInteractiveElement for GlassPill {}

impl ParentElement for GlassPill {
    fn extend(&mut self, elements: impl IntoIterator<Item = AnyElement>) {
        self.base.extend(elements);
    }
}

impl Selectable for GlassPill {
    fn selected(mut self, selected: bool) -> Self {
        self.selected = selected;
        self
    }

    fn is_selected(&self) -> bool {
        self.selected
    }
}

impl DropdownMenu for GlassPill {}

impl RenderOnce for GlassPill {
    fn render(mut self, _: &mut Window, cx: &mut App) -> impl IntoElement {
        let look = Look::of(cx);
        // Keep what the caller set (a size, padding, a gap) so it wins over the defaults put on below.
        let own = self.base.style().clone();
        let tooltip = self.tooltip;
        self.base
            .flex()
            .flex_none()
            .items_center()
            .h(BUTTON_HEIGHT)
            .rounded(CORNER_PILL)
            .border_1()
            .border_color(if self.outlined {
                look.on_glass_line_chip
            } else {
                transparent_black()
            })
            .text_color(look.on_glass_text_2)
            .cursor_pointer()
            .when(self.selected, |pill| {
                pill.bg(look.on_glass_fill_selected)
                    .text_color(look.on_glass_text_1)
            })
            .when(!self.selected, |pill| {
                pill.hover(|pill| pill.bg(look.fill_hover))
            })
            .refine_style(&own)
            .when_some(tooltip, |pill, words| {
                pill.tooltip(move |window, cx| Tooltip::new(words.clone()).build(window, cx))
            })
    }
}
