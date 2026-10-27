//! `ConfigMenu` is one of the agent's own menus (model, effort, mode…) from `config/get`, set through `config/set`.
//! It opens upward from the composer and flips down only when there's no room. They're read again when a turn ends,
//! since the agent can change a value itself. Replaces the config selects in Composer.svelte.

use std::rc::Rc;

use gpui_kit::*;

#[derive(IntoElement)]
pub struct ConfigMenu {
    pub config_id: String,
    pub label: SharedString,
    /// (value, label) for each choice.
    pub choices: Vec<(String, SharedString)>,
    pub current: String,
    /// The composer's handler, which sends `config/set` for (config id, value).
    pub on_select: Rc<dyn Fn(&str, &str, &mut Window, &mut App)>,
}

impl RenderOnce for ConfigMenu {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
    }
}
