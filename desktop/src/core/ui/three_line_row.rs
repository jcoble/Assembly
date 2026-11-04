//! The three-line list row, on the kit's list item, for anything shown that way: a lead mark, title, time and arrow;
//! small icons and a detail spelled out in a tooltip; and one more line of text. Everything but the marks, time and
//! arrow gives way as the list narrows. It takes display values only. `item` hands back the list item, so the owner
//! adds what its row does: selection, clicks and menus.

use std::rc::Rc;
use std::sync::Arc;

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::label::Label;
use gpui_kit::component::list::ListItem;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex, v_flex};
use gpui_kit::*;

use crate::core::state::preferences::Preferences;
use crate::core::theme::text::{TextRole, TextRoleExt as _};

/// Above and below a row's three lines.
const ROW_PADDING: Pixels = px(7.);

/// The row's arrow: which way it points and what pressing it does.
#[derive(Clone)]
pub struct RowArrow {
    pub open: bool,
    pub on_toggle: Rc<dyn Fn(&mut App)>,
}

pub struct ThreeLineRow {
    /// Unique in its list.
    pub key: SharedString,
    pub lead: AnyElement,
    pub title: SharedString,
    pub title_color: Hsla,
    pub time: Option<(SharedString, Hsla)>,
    pub arrow: Option<RowArrow>,
    /// Before the second line's text, and never squeezed by it.
    pub icons: Vec<AnyElement>,
    pub second: SharedString,
    /// The second line's tooltip.
    pub spelled_out: SharedString,
    pub third: SharedString,
    /// A dot in the left gutter, level with the title, so it doesn't move the title.
    pub dot: bool,
}

/// How tall a row is at the current text size, which is the step an open row's body grows in.
pub fn row_height(cx: &App) -> Pixels {
    let (title, meta) = Preferences::read(cx, |appearance| {
        (
            appearance.ui_text(TextRole::Title).1,
            appearance.ui_text(TextRole::Meta).1,
        )
    });
    title + meta * 2. + ROW_PADDING * 2. + px(4.)
}

/// The fill the kit's list item gives a selected row, for what has to match it, such as an open row's body.
pub fn selected_fill(cx: &App) -> Hsla {
    let theme = cx.theme();
    if theme.list.active_highlight {
        theme.list_active
    } else {
        theme.accent
    }
}

/// Figures of equal width, so a running time doesn't shift as it counts.
fn tabular() -> FontFeatures {
    FontFeatures(Arc::new(vec![("tnum".into(), 1)]))
}

impl ThreeLineRow {
    pub fn item(self, cx: &App) -> ListItem {
        let (_, title_height) =
            Preferences::read(cx, |appearance| appearance.ui_text(TextRole::Title));
        let theme = cx.theme();
        let (muted, accent, radius) = (theme.muted_foreground, theme.caret, theme.radius);
        let key = self.key;
        let arrow = self.arrow.map(|arrow| {
            Button::new(SharedString::from(format!("details:{key}")))
                .ghost()
                .xsmall()
                .icon(if arrow.open {
                    IconName::ChevronDown
                } else {
                    IconName::ChevronRight
                })
                .tooltip(if arrow.open {
                    "Hide details"
                } else {
                    "Show details"
                })
                .on_click(move |_, _, cx| {
                    cx.stop_propagation();
                    (arrow.on_toggle)(cx);
                })
        });
        let first = h_flex()
            .gap_2()
            .child(self.lead)
            .child(
                Label::new(self.title)
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .text_role(TextRole::Title, cx)
                    .font_weight(FontWeight::MEDIUM)
                    .text_color(self.title_color),
            )
            .child(
                h_flex()
                    .flex_none()
                    .gap_1()
                    .children(self.time.map(|(time, color)| {
                        Label::new(time)
                            .text_role(TextRole::Meta, cx)
                            .font_features(tabular())
                            .text_color(color)
                    }))
                    .children(arrow),
            );
        let spelled_out = self.spelled_out;
        let second = h_flex()
            .id(SharedString::from(format!("state:{key}")))
            .gap_1p5()
            .tooltip(move |window, cx| Tooltip::new(spelled_out.clone()).build(window, cx))
            .children(self.icons)
            .child(
                Label::new(self.second)
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .text_role(TextRole::Meta, cx)
                    .text_color(muted),
            );
        let third = Label::new(self.third)
            .w_full()
            .truncate()
            .text_role(TextRole::Meta, cx)
            .text_color(muted);
        let dot = self.dot.then(|| {
            div()
                .absolute()
                .left(px(3.))
                .top(ROW_PADDING + title_height * 0.5 - px(3.))
                .size(px(6.))
                .rounded_full()
                .bg(accent)
        });
        ListItem::new(key.clone())
            .w_full()
            .px_3()
            .py(ROW_PADDING)
            .rounded(radius)
            .children(dot)
            // Zero wide but at least full: the kit's wrapper would otherwise hold the row at its text's width.
            .child(
                v_flex()
                    .w_0()
                    .min_w_full()
                    .gap_0p5()
                    .child(first)
                    .child(second)
                    .child(third),
            )
    }
}

#[cfg(test)]
#[path = "three_line_row_tests.rs"]
mod tests;
