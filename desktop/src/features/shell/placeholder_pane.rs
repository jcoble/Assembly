//! `PlaceholderPane` owns an empty pane for one name, until the lane that owns the pane registers its real one under
//! the same name (the kit's registry keeps the last registration). It draws nothing, so nothing is mounted while
//! hidden. Replaces PanelPlaceholder.svelte.

use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent};
use gpui_kit::*;

pub struct PlaceholderPane {
    name: &'static str,
    focus: FocusHandle,
}

impl PlaceholderPane {
    pub fn new(_name: &'static str, _cx: &mut Context<Self>) -> Self {
        todo!()
    }

    /// Registers a placeholder under each of the default layout's pane names, so a saved layout loads.
    pub fn register_all(_cx: &mut App) {
        todo!()
    }
}

impl EventEmitter<PanelEvent> for PlaceholderPane {}

impl Focusable for PlaceholderPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for PlaceholderPane {
    fn panel_name(&self) -> &'static str {
        self.name
    }

    /// The default layout's panes are permanent tabs, with no close button.
    fn closable(&self, _cx: &App) -> bool {
        false
    }
}

impl Panel for PlaceholderPane {}

impl Render for PlaceholderPane {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
