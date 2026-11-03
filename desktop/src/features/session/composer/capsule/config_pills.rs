//! The agent's settings as the composer's two pills. One says how much the agent may do without asking; the other reads
//! as "Model · Effort" and opens to Model and Effort, with any other select the provider reports below them so nothing
//! it offers is lost. Both open upward, away from the window's edge, and a narrow composer folds them into one ⋯ menu.
//! Each choice is the agent's own config id and value, set through the same call the composer always used.

use std::rc::Rc;

use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::button::{Button, ButtonRounded, ButtonVariants as _};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenu, PopupMenuItem};
use gpui_kit::component::spinner::Spinner;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::*;

use super::config_labels::{
    approval_description, approval_label, effort_label, model_effort_label, model_label,
};
use crate::features::session::composer::agent_config::ConfigOption;

const CHIP_HEIGHT: Pixels = px(28.);

pub type OnSelect = Rc<dyn Fn(&str, &str, &mut Window, &mut App)>;

#[derive(IntoElement)]
pub struct ConfigPills {
    /// The agent's name, for the menus' headings and the model rows.
    pub provider: SharedString,
    pub options: Vec<(ConfigOption, String)>,
    pub on_select: OnSelect,
    pub disabled: bool,
    /// A pick is still being written: approvals hold and "Saving…" shows.
    pub saving: bool,
    pub compact: bool,
}

impl RenderOnce for ConfigPills {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let saving = self.saving.then(|| {
            h_flex()
                .gap_1()
                .text_size(px(13.))
                .text_color(theme.primary)
                .child(Spinner::new().xsmall())
                .child("Saving…")
        });
        let row = h_flex().flex_none().min_w_0().gap_1().children(saving);
        let grouped = Grouped::of(self.options);
        if self.compact {
            return row.child(compact(grouped, self.provider, self.on_select, self.disabled));
        }
        let approval = grouped.approval.clone().map(|(option, current)| {
            let (provider, on_select) = (self.provider.clone(), self.on_select.clone());
            let label = approval_label(Some(&option), shown(&option, &current));
            chip("composer:approval")
                .icon(Icon::new(Glyph::ShieldCheck).size(px(14.)))
                .label(label)
                .disabled(self.disabled || self.saving)
                .dropdown_menu_with_anchor(Anchor::BottomLeft, move |menu, _, _| {
                    let menu = menu.min_w(px(248.)).max_w(px(320.));
                    approval_rows(menu, &provider, &option, &current, &on_select)
                })
        });
        let (provider, on_select) = (self.provider, self.on_select);
        let label = grouped.pill_label();
        let empty = grouped.model.is_none() && grouped.effort.is_none() && grouped.others.is_empty();
        let model = (!empty).then(|| {
            chip("composer:model")
                .label(label)
                .disabled(self.disabled)
                .dropdown_menu_with_anchor(Anchor::BottomRight, move |menu, window, cx| {
                    grouped.model_menu(menu.min_w(px(220.)), &provider, &on_select, window, cx)
                })
        });
        row.children(approval).children(model)
    }
}

/// The options sorted into the pills they belong to.
struct Grouped {
    approval: Option<(ConfigOption, String)>,
    model: Option<(ConfigOption, String)>,
    effort: Option<(ConfigOption, String)>,
    others: Vec<(ConfigOption, String)>,
}

impl Grouped {
    fn of(options: Vec<(ConfigOption, String)>) -> Self {
        let mut grouped = Grouped {
            approval: None,
            model: None,
            effort: None,
            others: Vec::new(),
        };
        for (option, current) in options {
            let slot = if option.model() {
                &mut grouped.model
            } else if option.effort() {
                &mut grouped.effort
            } else if option.approval() {
                &mut grouped.approval
            } else {
                grouped.others.push((option, current));
                continue;
            };
            if slot.is_some() {
                grouped.others.push((option, current));
            } else {
                *slot = Some((option, current));
            }
        }
        grouped
    }

    fn pill_label(&self) -> String {
        model_effort_label(
            self.model.as_ref().and_then(|(option, current)| {
                let value = shown(option, current);
                (!value.is_empty()).then(|| model_label(option, value))
            }),
            self.effort.as_ref().and_then(|(option, current)| {
                let value = shown(option, current);
                (!value.is_empty()).then(|| effort_label(option, value))
            }),
        )
    }

    fn model_menu(
        &self,
        mut menu: PopupMenu,
        provider: &SharedString,
        on_select: &OnSelect,
        window: &mut Window,
        cx: &mut Context<PopupMenu>,
    ) -> PopupMenu {
        if let Some((option, current)) = self.model.clone() {
            let label = format!("Model · {}", model_label(&option, shown(&option, &current)));
            let (provider, on_select) = (provider.clone(), on_select.clone());
            menu = menu.submenu(label, window, cx, move |menu, _, _| {
                model_rows(menu.min_w(px(240.)), &provider, &option, &current, &on_select)
            });
        }
        if let Some((option, current)) = self.effort.clone() {
            if self.model.is_some() {
                menu = menu.separator();
            }
            let label = format!("Effort · {}", effort_label(&option, shown(&option, &current)));
            let on_select = on_select.clone();
            menu = menu.submenu(label, window, cx, move |menu, _, _| {
                effort_rows(menu.min_w(px(180.)), &option, &current, &on_select)
            });
        }
        for (option, current) in self.others.clone() {
            let label = format!("{} · {}", option.name, provider_value(&option, &current));
            let on_select = on_select.clone();
            menu = menu.submenu(label, window, cx, move |menu, _, _| {
                other_rows(menu.min_w(px(200.)), &option, &current, &on_select)
            });
        }
        menu
    }
}

fn compact(
    grouped: Grouped,
    provider: SharedString,
    on_select: OnSelect,
    disabled: bool,
) -> impl IntoElement {
    chip("composer:controls")
        .icon(Icon::new(Glyph::Ellipsis).size(px(16.)))
        .accessibility_label("More composer controls")
        .disabled(disabled)
        .dropdown_menu_with_anchor(Anchor::BottomRight, move |menu, _, _| {
            let mut menu = menu.min_w(px(248.)).max_w(px(320.)).label("Composer controls");
            if let Some((option, current)) = &grouped.model {
                menu = model_rows(menu.label("Model"), &provider, option, current, &on_select);
            }
            if let Some((option, current)) = &grouped.effort {
                menu = effort_rows(menu.separator().label("Thinking effort"), option, current, &on_select);
            }
            if let Some((option, current)) = &grouped.approval {
                menu = menu.separator().label(format!("Access for {provider}"));
                menu = approval_choices(menu, option, current, &on_select);
            }
            for (option, current) in &grouped.others {
                menu = other_rows(menu.separator().label(option.name.clone()), option, current, &on_select);
            }
            menu
        })
}

fn chip(id: &'static str) -> Button {
    Button::new(id)
        .ghost()
        .small()
        .h(CHIP_HEIGHT)
        .rounded(ButtonRounded::Size(px(999.)))
        .dropdown_caret(true)
}

/// The value to read back: the one offered now, or what the agent reported even if it no longer offers it.
fn shown<'a>(option: &'a ConfigOption, current: &'a str) -> &'a str {
    if current.is_empty() {
        option.current_value.as_deref().unwrap_or_default()
    } else {
        current
    }
}

fn provider_value(option: &ConfigOption, current: &str) -> String {
    let value = shown(option, current);
    option
        .options
        .iter()
        .find(|choice| choice.value == value)
        .map_or_else(|| value.to_string(), |choice| choice.name.clone())
}

fn pick(
    option: &ConfigOption,
    value: &str,
    on_select: &OnSelect,
) -> impl Fn(&ClickEvent, &mut Window, &mut App) + 'static {
    let (id, value, on_select) = (option.id.clone(), value.to_string(), on_select.clone());
    move |_, window, cx| on_select(&id, &value, window, cx)
}

/// A menu row with a smaller line under its name.
fn described(title: String, below: Option<String>) -> PopupMenuItem {
    PopupMenuItem::element(move |_, cx| {
        let muted = cx.theme().muted_foreground;
        v_flex()
            .gap(px(2.))
            .child(title.clone())
            .children(below.clone().map(|below| {
                div()
                    .text_size(px(13.))
                    .line_height(relative(1.4))
                    .text_color(muted)
                    .child(below)
            }))
    })
}

fn approval_rows(
    menu: PopupMenu,
    provider: &SharedString,
    option: &ConfigOption,
    current: &str,
    on_select: &OnSelect,
) -> PopupMenu {
    let menu = menu.label(format!("When {provider} needs permission"));
    approval_choices(menu, option, current, on_select)
}

fn approval_choices(
    menu: PopupMenu,
    option: &ConfigOption,
    current: &str,
    on_select: &OnSelect,
) -> PopupMenu {
    option.options.iter().fold(menu, |menu, choice| {
        menu.item(
            described(
                approval_label(Some(option), &choice.value),
                approval_description(choice),
            )
            .checked(choice.value == current)
            .on_click(pick(option, &choice.value, on_select)),
        )
    })
}

fn model_rows(
    mut menu: PopupMenu,
    provider: &SharedString,
    option: &ConfigOption,
    current: &str,
    on_select: &OnSelect,
) -> PopupMenu {
    // A check mark against nothing would mislead: what the agent reports shows even when it's no longer offered.
    if let Some(gone) = option
        .current_value
        .as_deref()
        .filter(|value| !option.offered(value))
    {
        menu = menu
            .item(
                described(model_label(option, gone), Some(provider.to_string()))
                    .checked(true)
                    .disabled(true),
            )
            .item(PopupMenuItem::new("Unavailable for this session.").disabled(true));
    }
    option.options.iter().fold(menu, |menu, choice| {
        menu.item(
            described(model_label(option, &choice.value), Some(provider.to_string()))
                .checked(choice.value == current)
                .on_click(pick(option, &choice.value, on_select)),
        )
    })
}

fn effort_rows(
    menu: PopupMenu,
    option: &ConfigOption,
    current: &str,
    on_select: &OnSelect,
) -> PopupMenu {
    option.options.iter().fold(menu, |menu, choice| {
        menu.item(
            PopupMenuItem::new(effort_label(option, &choice.value))
                .checked(choice.value == current)
                .on_click(pick(option, &choice.value, on_select)),
        )
    })
}

fn other_rows(
    menu: PopupMenu,
    option: &ConfigOption,
    current: &str,
    on_select: &OnSelect,
) -> PopupMenu {
    option.options.iter().fold(menu, |menu, choice| {
        menu.item(
            described(choice.name.clone(), choice.description.clone())
                .checked(choice.value == current)
                .on_click(pick(option, &choice.value, on_select)),
        )
    })
}

#[cfg(test)]
#[path = "config_pills_tests.rs"]
mod tests;
