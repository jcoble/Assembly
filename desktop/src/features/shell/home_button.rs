//! `HomeButton` owns the Home menu: Projects, Messages, Settings and Update. Each item opens a screen or dialog that
//! a later lane owns. Replaces HomeButton.svelte.

use gpui_kit::*;

pub struct HomeButton;

impl Render for HomeButton {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
