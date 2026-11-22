//! The three-line list row, on the kit's list item, for anything shown that way, as the look's session row draws it:
//! a lead glyph in its own column, then the title (Body 500) with its time and arrow; small icons and a detail in
//! Meta text-3, spelled out in a tooltip; and one more line in Meta text-2. Everything but the marks, time and arrow
//! gives way as the list narrows. It takes display values only. `item` hands back the list item, so the owner adds
//! what its row does: selection, clicks and menus.

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
use crate::core::theme::look::Look;
use crate::core::theme::metrics::{CORNER_FIELD, SPACE};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::row;

/// Above and below a row's three lines, and before its glyph (the mockup's `.srow`, 7 10 7 9).
const ROW_PADDING: Pixels = px(7.);
const ROW_PADDING_LEFT: Pixels = px(9.);
/// Between the second line's icons and words.
const SECOND_GAP: Pixels = px(5.);

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
            appearance.ui_text(TextRole::Body).1,
            appearance.ui_text(TextRole::Meta).1,
        )
    });
    title + meta * 2. + ROW_PADDING * 2.
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
pub fn tabular() -> FontFeatures {
    FontFeatures(Arc::new(vec![("tnum".into(), 1)]))
}

impl ThreeLineRow {
    pub fn item(self, cx: &App) -> ListItem {
        self.item_with_action(None, cx)
    }

    /// Gives an optional action its own space beside the details and preview, within the row's surface.
    pub fn item_with_action(self, action: Option<AnyElement>, cx: &App) -> ListItem {
        let (_, title_height) =
            Preferences::read(cx, |appearance| appearance.ui_text(TextRole::Body));
        let look = Look::of(cx);
        let key = self.key;
        let spoken = format!("{}, {}", self.title, self.spelled_out);
        let arrow = self.arrow.map(|arrow| {
            Button::new(SharedString::from(format!("details:{key}")))
                .control(
                    format!("{}.details", row(&key)),
                    if arrow.open {
                        "Hide details"
                    } else {
                        "Show details"
                    },
                )
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
            .gap(SPACE[3])
            .child(
                Label::new(self.title)
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .text_role(TextRole::Body, cx)
                    .font_weight(FontWeight::MEDIUM)
                    .text_color(self.title_color),
            )
            .child(
                h_flex()
                    .flex_none()
                    .gap(SPACE[1])
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
            .gap(SECOND_GAP)
            .tooltip(move |window, cx| Tooltip::new(spelled_out.clone()).build(window, cx))
            .children(self.icons)
            .child(
                Label::new(self.second)
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .text_role(TextRole::Meta, cx)
                    .text_color(look.text_3),
            );
        let third = Label::new(self.third)
            .w_full()
            .truncate()
            .text_role(TextRole::Meta, cx)
            .text_color(look.text_2);
        let details = h_flex()
            .items_end()
            .gap(SPACE[4])
            .child(v_flex().flex_1().min_w_0().child(second).child(third))
            .children(action);
        // The unread dot sits in the left padding, level with the title, so it doesn't move anything.
        let dot = self.dot.then(|| {
            div()
                .absolute()
                .left(SPACE[0])
                .top(ROW_PADDING + title_height * 0.5 - SPACE[0] * 1.5)
                .size(SPACE[2])
                .rounded_full()
                .bg(look.accent)
        });
        ListItem::new(key.clone())
            .control(row(&key), spoken)
            .w_full()
            .pl(ROW_PADDING_LEFT)
            .pr(SPACE[4])
            .py(ROW_PADDING)
            .rounded(CORNER_FIELD)
            .children(dot)
            // The glyph in its own column, 10 before the lines and 2 down so it sits level with the title.
            // Zero wide but at least full: the kit's wrapper would otherwise hold the row at its text's width.
            .child(
                h_flex()
                    .w_0()
                    .min_w_full()
                    .items_start()
                    .gap(SPACE[4])
                    .child(div().flex_none().mt(SPACE[0]).child(self.lead))
                    .child(v_flex().flex_1().min_w_0().child(first).child(details)),
            )
    }
}

#[cfg(test)]
#[path = "three_line_row_tests.rs"]
mod tests;
