//! The Settings tab's content: one kit page per section, its cards as groups, and each row as an item with its title and
//! one-line description beside its control, or its slider underneath.
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::component::button::Button;
use gpui_kit::component::input::Input;
use gpui_kit::component::label::Label;
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::setting::{SettingField, SettingGroup, SettingItem, SettingPage};
use gpui_kit::component::slider::Slider;
use gpui_kit::component::{ActiveTheme as _, Sizable as _};
use gpui_kit::component::{h_flex, v_flex};
use gpui_kit::*;
use serde_json::{Value, json};

use super::fonts;
use super::row::{ROWS, Row};
use super::section::{SECTIONS, Section};
use super::settings_page::SettingsPage;
use crate::core::state::appearance::{
    CODE_FONT, EDITOR_FONT, EDITOR_FONT_SIZE, EDITOR_LINE_HEIGHT, NumberSetting, TERMINAL_FONT,
    TERMINAL_FONT_SIZE, TERMINAL_LINE_HEIGHT, THEME, UI_FONT, UI_FONT_SIZE, UI_LINE_HEIGHT,
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
    /// One kit page per section, in the old app's order, each with its cards as groups and its rows as items. The search
    /// box above the sections matches a row's title, description, words and card, and the section's name.
    pub(super) fn pages(&self, cx: &mut Context<Self>) -> Vec<SettingPage> {
        let failure = Preferences::failure(cx);
        SECTIONS
            .iter()
            .map(|section| {
                let mut page = SettingPage::new(section.label)
                    .icon(section.icon)
                    .resettable(false);
                if let Some(failure) = failure.clone() {
                    // Beside the title, which a search leaves in place.
                    page = page.title_suffix(move |_, cx| {
                        Label::new(failure.clone())
                            .text_role(TextRole::Meta, cx)
                            .text_color(cx.theme().danger)
                    });
                }
                let mut cards: Vec<(&str, SettingGroup)> = Vec::new();
                for row in ROWS.iter().filter(|row| row.section == section.id) {
                    let item = self.item(row, section, cx);
                    match cards.iter_mut().find(|(card, _)| *card == row.card) {
                        Some((_, group)) => *group = group.clone().item(item),
                        None => cards.push((row.card, group().title(row.card).item(item))),
                    }
                }
                if cards.is_empty() {
                    return page.group(group().item(SettingItem::render(|_, _, cx| {
                        note(
                            "This section isn't in this window yet; it comes with a later change.",
                            cx,
                        )
                    })));
                }
                page.groups(cards.into_iter().map(|(_, group)| group))
            })
            .collect()
    }

    /// A row as a kit item: its title and description beside its control, a slider under them, or the machine cards.
    fn item(
        &self,
        row: &'static Row,
        section: &'static Section,
        cx: &mut Context<Self>,
    ) -> SettingItem {
        let page = cx.entity();
        let words = [
            row.title,
            row.description,
            row.keywords,
            row.card,
            section.label,
        ];
        if row.id == "machines" {
            return SettingItem::render(move |_, window, cx| {
                page.update(cx, |page, cx| {
                    page.machines_drawn = true;
                    page.follow_ssh_hosts(true, window, cx);
                    page.machines(cx)
                })
            })
            .keywords(words);
        }
        let control =
            SettingField::render(move |_, _, cx| page.update(cx, |page, cx| page.control(row, cx)));
        let item = SettingItem::new(row.title, control)
            .description(row.description)
            .keywords(words);
        if number_row(row.id).is_some() {
            item.layout(Axis::Vertical)
        } else {
            item
        }
    }

    /// The control a row's item draws: a slider with its value, the depth box, or a menu.
    fn control(&self, row: &'static Row, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme();
        let number = number_row(row.id).map(|number| number.key);
        let slider = self
            .sliders
            .iter()
            .find(|(setting, _)| number == Some(setting.key));
        if let Some((setting, state)) = slider {
            let value = SettingsPage::setting_value(*setting, cx);
            return h_flex()
                .w_full()
                .gap_4()
                .child(div().flex_1().child(Slider::new(state)))
                .child(
                    div()
                        .w(px(44.))
                        .text_right()
                        .text_role(TextRole::Meta, cx)
                        .text_color(theme.muted_foreground)
                        .child(setting.label(value)),
                )
                .into_any_element();
        }
        let control = match row.id {
            "session-depth-limit" => return self.depth_control(cx).into_any_element(),
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
        div().w(CONTROL_WIDTH).child(control).into_any_element()
    }
}

/// A card, as wide as the old app's content column.
fn group() -> SettingGroup {
    SettingGroup::new().w_full().max_w(COLUMN_WIDTH)
}

/// The number a slider row sets.
fn number_row(id: &str) -> Option<NumberSetting> {
    Some(match id {
        "ui-font-size" => UI_FONT_SIZE,
        "ui-line-height" => UI_LINE_HEIGHT,
        "editor-font-size" => EDITOR_FONT_SIZE,
        "editor-line-height" => EDITOR_LINE_HEIGHT,
        "terminal-font-size" => TERMINAL_FONT_SIZE,
        "terminal-line-height" => TERMINAL_LINE_HEIGHT,
        "session-child-limit" => crate::core::state::sessions_settings::CHILD_LIMIT,
        _ => return None,
    })
}

impl SettingsPage {
    fn depth_control(&self, cx: &App) -> Div {
        let mut control = v_flex()
            .w(CONTROL_WIDTH)
            .gap_1()
            .child(Input::new(&self.depth).small());
        if let Some(error) = self.depth_error {
            control = control.child(
                div()
                    .text_role(TextRole::Meta, cx)
                    .text_color(cx.theme().danger)
                    .child(error),
            );
        }
        control
    }
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

fn note(text: &'static str, cx: &App) -> Div {
    div()
        .pt_2()
        .text_role(TextRole::Meta, cx)
        .text_color(cx.theme().muted_foreground)
        .child(text)
}
