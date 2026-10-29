//! `ConfigMenu` is one of the agent's own menus (model, effort, mode…) from `config/get`, set through `config/set`:
//! an outlined button in the composer's footer showing the menu's name, muted, then the value chosen now, opening
//! upward with a check on that value. The kit's menu doesn't flip, so it opens upward even in a window too short for
//! it; the composer sits at the bottom, so there's room above in any usable window. They're read again when a turn
//! ends, since the agent can change a value itself. Replaces the config selects in Composer.svelte.

use std::rc::Rc;

use gpui_kit::component::button::Button;
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::{ActiveTheme as _, Sizable as _};
use gpui_kit::*;

/// Takes (config id, value).
pub type OnSelect = Rc<dyn Fn(&str, &str, &mut Window, &mut App)>;

#[derive(IntoElement)]
pub struct ConfigMenu {
    pub config_id: String,
    pub label: SharedString,
    /// (value, label) for each choice.
    pub choices: Vec<(String, SharedString)>,
    pub current: String,
    /// The composer's handler, which sends `config/set` for (config id, value).
    pub on_select: OnSelect,
}

impl RenderOnce for ConfigMenu {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let shown = self
            .choices
            .iter()
            .find(|(value, _)| *value == self.current)
            .map(|(_, name)| name.clone());
        let ConfigMenu {
            config_id,
            label,
            choices,
            current,
            on_select,
        } = self;
        // "Effort  Medium": the menu's name, muted, then the value chosen now, cut off with an ellipsis past 96 px.
        let name = div().text_color(cx.theme().muted_foreground).child(label);
        Button::new(SharedString::from(format!("composer:menu:{config_id}")))
            .outline()
            .small()
            .rounded(cx.theme().radius)
            .child(name)
            .children(shown.map(|shown| div().max_w(px(96.)).truncate().child(shown)))
            .dropdown_caret(true)
            .dropdown_menu_with_anchor(Anchor::BottomLeft, move |menu, _, _| {
                choices.iter().fold(menu, |menu, (value, name)| {
                    let (id, value, on_select) =
                        (config_id.clone(), value.clone(), on_select.clone());
                    menu.item(
                        PopupMenuItem::new(name.clone())
                            .checked(value == current)
                            .on_click(move |_, window, cx| on_select(&id, &value, window, cx)),
                    )
                })
            })
    }
}
