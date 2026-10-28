//! The right side of the Settings page: the shown section's title, then its rows in their cards,
//! each row a title and one-line description with its control on the right, or its slider underneath.

use gpui_kit::component::button::Button;
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::slider::Slider;
use gpui_kit::component::{ActiveTheme as _, Sizable as _};
use gpui_kit::component::{h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::{Value, json};

use super::fonts;
use super::row::Row;
use super::search;
use super::section::Section;
use super::settings_page::SettingsPage;
use crate::core::state::appearance::{
    CODE_FONT, EDITOR_FONT, EDITOR_FONT_SIZE, EDITOR_LINE_HEIGHT, NumberSetting, TERMINAL_FONT,
    TERMINAL_FONT_SIZE, TERMINAL_LINE_HEIGHT, THEME, UI_FONT, UI_FONT_SIZE,
};
use crate::core::state::preferences::Preferences;
use crate::core::theme::theme_id::ThemeId;

/// The content column's width, the old app's.
const COLUMN_WIDTH: Pixels = px(720.);
/// A row's control on the right.
const CONTROL_WIDTH: Pixels = px(220.);
/// What the editor's and terminal's face menu offers first: no face of their own.
const SAME_AS_CODE_FONT: &str = "Same as Code font";

impl SettingsPage {
    pub(super) fn content(
        &self,
        query: &str,
        shown: Option<&Section>,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let theme = cx.theme().clone();
        let column = v_flex()
            .w_full()
            .max_w(COLUMN_WIDTH)
            .mx_auto()
            .px_4()
            .pt(px(28.))
            .pb_10();
        let Some(section) = shown else {
            return column.child(
                div()
                    .text_sm()
                    .text_color(theme.muted_foreground)
                    .child(format!("Nothing matches “{}”.", query.trim())),
            );
        };
        let cards = search::cards(query, section.id);
        let id = section.id;
        let mut column = column.child(
            h_flex()
                .h(px(32.))
                .text_size(px(15.))
                .font_weight(FontWeight::SEMIBOLD)
                .text_color(theme.foreground)
                .child(section.label),
        );
        if let Some(failure) = Preferences::failure(cx) {
            column = column.child(
                div()
                    .pt_2()
                    .text_xs()
                    .text_color(theme.danger)
                    .child(failure),
            );
        }
        if id == "terminal" {
            column = column.child(note(
                "The terminal isn't in this window yet. These are saved now and apply once it is.",
                cx,
            ));
        }
        if cards.is_empty() {
            return column.child(note(
                "This section isn't in this window yet; it comes with a later change.",
                cx,
            ));
        }
        for (card, rows) in cards {
            let mut body = v_flex()
                .bg(theme.group_box)
                .border_1()
                .border_color(theme.border)
                .rounded(px(8.));
            for (ix, row) in rows.into_iter().enumerate() {
                body = body.child(self.row(row, cx).when(ix > 0, |row| {
                    row.border_t_1().border_color(theme.border.opacity(0.6))
                }));
            }
            column = column
                .child(
                    div()
                        .pt_5()
                        .pb_1p5()
                        .px_1()
                        .text_xs()
                        .text_color(theme.muted_foreground)
                        .child(card),
                )
                .child(body);
        }
        column
    }

    fn row(&self, row: &'static Row, cx: &mut Context<Self>) -> Div {
        let theme = cx.theme();
        let label = v_flex()
            .gap_0p5()
            .min_w_0()
            .child(
                div()
                    .text_sm()
                    .text_color(theme.foreground)
                    .child(row.title),
            )
            .child(
                div()
                    .text_xs()
                    .text_color(theme.muted_foreground)
                    .child(row.description),
            );
        let number = number_row(row.id).map(|number| number.key);
        let slider = self
            .sliders
            .iter()
            .find(|(setting, _)| number == Some(setting.key));
        if let Some((setting, state)) = slider {
            let value = Preferences::read(cx, |appearance| appearance.number(setting));
            return v_flex().px_4().py_3().gap_3().child(label).child(
                h_flex()
                    .gap_4()
                    .child(div().flex_1().child(Slider::new(state)))
                    .child(
                        div()
                            .w(px(44.))
                            .text_right()
                            .text_xs()
                            .text_color(theme.muted_foreground)
                            .child(setting.label(value)),
                    ),
            );
        }
        let control = match row.id {
            "machines" => return self.machines(cx),
            "theme" => {
                let current = Preferences::read(cx, |appearance| appearance.theme());
                let choices = ThemeId::ALL
                    .map(|id| (json!(id.as_str()), id.theme_name().into(), id == current));
                menu(row.id, current.theme_name().into(), choices.into(), THEME)
            }
            "ui-font" => {
                let current = Preferences::read(cx, |appearance| appearance.ui_font());
                menu(
                    row.id,
                    fonts::label(&current),
                    font_choices(&self.ui_fonts, &current),
                    UI_FONT,
                )
            }
            "code-font" => {
                let current = Preferences::read(cx, |appearance| appearance.code_font());
                menu(
                    row.id,
                    fonts::label(&current),
                    font_choices(&self.code_fonts, &current),
                    CODE_FONT,
                )
            }
            _ => {
                let key = if row.id == "editor-font" {
                    EDITOR_FONT
                } else {
                    TERMINAL_FONT
                };
                let own = Preferences::read(cx, |appearance| appearance.font(key));
                let mut choices = vec![(Value::Null, SAME_AS_CODE_FONT.into(), own.is_none())];
                choices.extend(font_choices(
                    &self.code_fonts,
                    own.as_deref().unwrap_or_default(),
                ));
                let shown = own.map_or(SAME_AS_CODE_FONT.into(), |own| fonts::label(&own));
                menu(row.id, shown, choices, key)
            }
        };
        h_flex()
            .px_4()
            .py_3()
            .gap_4()
            .justify_between()
            .child(label)
            .child(div().w(CONTROL_WIDTH).flex_none().child(control))
    }
}

/// The number a slider row sets.
fn number_row(id: &str) -> Option<NumberSetting> {
    Some(match id {
        "ui-font-size" => UI_FONT_SIZE,
        "editor-font-size" => EDITOR_FONT_SIZE,
        "editor-line-height" => EDITOR_LINE_HEIGHT,
        "terminal-font-size" => TERMINAL_FONT_SIZE,
        "terminal-line-height" => TERMINAL_LINE_HEIGHT,
        _ => return None,
    })
}

fn font_choices(fonts: &[SharedString], current: &str) -> Vec<(Value, SharedString, bool)> {
    fonts
        .iter()
        .map(|name| {
            (
                json!(name.as_ref()),
                fonts::label(name),
                name.as_ref() == current,
            )
        })
        .collect()
}

/// A select: the shown choice with a caret, opening a menu of the choices that saves the one picked under `key`.
fn menu(
    id: &'static str,
    shown: SharedString,
    choices: Vec<(Value, SharedString, bool)>,
    key: &'static str,
) -> impl IntoElement {
    Button::new(id)
        .outline()
        .small()
        .w_full()
        .justify_between()
        .label(shown)
        .dropdown_caret(true)
        .dropdown_menu_with_anchor(Anchor::TopRight, move |menu, _, _| {
            choices.iter().fold(
                menu.scrollable(true).max_h(px(360.)),
                |menu, (value, label, checked)| {
                    let value = value.clone();
                    menu.item(
                        PopupMenuItem::new(label.clone())
                            .checked(*checked)
                            .on_click(move |_, _, cx| Preferences::save(key, value.clone(), cx)),
                    )
                },
            )
        })
}

fn note(text: &'static str, cx: &App) -> impl IntoElement {
    div()
        .pt_2()
        .text_xs()
        .text_color(cx.theme().muted_foreground)
        .child(text)
}
