//! Provider config value pickers, using the kit's Select popup, keyboard navigation and selection.
//! Retain one picker per session/config ID; provider-confirmed values remain authoritative.

use std::rc::Rc;

use super::agent_config::ConfigOption;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::Button;
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};

use gpui_kit::component::select::{Select, SelectEvent, SelectItem, SelectState};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _};
use gpui_kit::*;

pub type OnSelect = Rc<dyn Fn(&str, &str, &mut Window, &mut App)>;

/// Model choices stay in the footer; all other provider settings live in submenus.
pub fn inline(option: &ConfigOption) -> bool {
    option.category.as_deref() == Some("model") || option.id == "model"
}

#[derive(IntoElement)]
pub struct ConfigSettings {
    pub options: Vec<(ConfigOption, String)>,
    pub on_select: OnSelect,
    pub disabled: bool,
}

impl RenderOnce for ConfigSettings {
    fn render(self, _: &mut Window, _: &mut App) -> impl IntoElement {
        Button::new("composer:settings")
            .disabled(self.disabled)
            .icon(IconName::Settings)
            .small()
            .size(px(30.))
            .rounded(px(9.))
            .accessibility_label("Session settings")
            .tooltip("Session settings")
            .dropdown_menu_with_anchor(Anchor::BottomLeft, move |menu, window, cx| {
                self.options
                    .iter()
                    .fold(menu.min_w(px(240.)), |menu, (option, current)| {
                        let shown = option
                            .options
                            .iter()
                            .find(|choice| choice.value == *current)
                            .map_or(current.as_str(), |choice| choice.name.as_str());
                        let label = format!("{}: {}", option.name, shown);
                        let choices = option.options.clone();
                        let config_id = option.id.clone();
                        let current = current.clone();
                        let on_select = self.on_select.clone();
                        menu.submenu(label, window, cx, move |menu, _, _| {
                            choices.iter().fold(menu.min_w(px(220.)), |menu, choice| {
                                let (id, value, on_select) =
                                    (config_id.clone(), choice.value.clone(), on_select.clone());
                                menu.item(
                                    PopupMenuItem::new(choice.name.clone())
                                        .checked(choice.value == current)
                                        .on_click(move |_, window, cx| {
                                            on_select(&id, &value, window, cx)
                                        }),
                                )
                            })
                        })
                    })
            })
    }
}

#[derive(Clone, PartialEq)]
struct Choice {
    value: String,
    label: SharedString,
}

impl SelectItem for Choice {
    type Value = String;
    fn title(&self) -> SharedString {
        self.label.clone()
    }
    fn value(&self) -> &String {
        &self.value
    }
}

struct Picker {
    select: Entity<SelectState<Vec<Choice>>>,
    choices: Vec<Choice>,
    on_select: OnSelect,
    _subscription: Subscription,
}

#[derive(IntoElement)]
pub struct ConfigMenu {
    pub session: String,
    pub config_id: String,
    pub label: SharedString,
    pub choices: Vec<(String, SharedString)>,
    pub current: String,
    pub on_select: OnSelect,
    pub disabled: bool,
}

impl RenderOnce for ConfigMenu {
    fn render(self, window: &mut Window, cx: &mut App) -> impl IntoElement {
        let choices: Vec<_> = self
            .choices
            .into_iter()
            .map(|(value, label)| Choice { value, label })
            .collect();
        let id = SharedString::from(format!(
            "composer:select:{}:{}",
            self.session, self.config_id
        ));
        let state = window.use_keyed_state(id.clone(), cx, {
            let choices = choices.clone();
            let current = self.current.clone();
            let config_id = self.config_id.clone();
            let on_select = self.on_select.clone();
            move |window, cx| {
                let select = cx.new(|cx| {
                    let mut select = SelectState::new(choices.clone(), None, window, cx);
                    select.set_selected_value(&current, window, cx);
                    select
                });
                let subscription = cx.subscribe_in(
                    &select,
                    window,
                    move |picker: &mut Picker, _, event, window, cx| {
                        if let SelectEvent::Confirm(Some(value)) = event {
                            (picker.on_select)(&config_id, value, window, cx);
                        }
                    },
                );
                Picker {
                    select,
                    choices,
                    on_select,
                    _subscription: subscription,
                }
            }
        });
        state.update(cx, |picker, cx| {
            picker.on_select = self.on_select;
            let changed = picker.choices != choices;
            let selection_changed = picker.select.read(cx).selected_value() != Some(&self.current);
            if changed || selection_changed {
                picker.select.update(cx, |select, cx| {
                    if changed {
                        select.set_items(choices.clone(), window, cx);
                    }
                    select.set_selected_value(&self.current, window, cx);
                });
                picker.choices = choices;
            }
        });
        let select = state.read(cx).select.clone();
        let theme = cx.theme();
        // The kit Select root fills its parent; bound that parent before the footer wraps it.
        div().flex_none().h(px(30.)).w(px(200.)).child(
            Select::new(&select)
                .disabled(self.disabled)
                .id(id)
                .accessibility_label(self.label.clone())
                .placeholder(self.label.clone())
                .small()
                .h_full()
                .w_full()
                .rounded(px(9.))
                .bg(theme.accent)
                .text_size(px(12.5))
                .menu_width(px(240.))
                .menu_max_h(px(280.)),
        )
    }
}
