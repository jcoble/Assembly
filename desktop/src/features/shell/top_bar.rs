//! `TopBar` owns the row above the panes: the Home button, which keeps the sessions column's width so the dividers
//! line up, the `+` open menu and the drawer toggle. The tabs themselves are the DockArea's tab bars. Replaces
//! TopTabRow.svelte's chrome and the `window-chrome` markup in routes/+page.svelte.

use gpui_kit::component::dock::DockArea;
use gpui_kit::*;

use super::home_button::HomeButton;

pub struct TopBar {
    dock: Entity<DockArea>,
    home: Entity<HomeButton>,
}

impl TopBar {
    pub fn new(_dock: Entity<DockArea>, _cx: &mut Context<Self>) -> Self {
        todo!()
    }
}

impl Render for TopBar {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
