//! `HomeButton` owns the Home menu: Projects, Messages, Settings and Update. Each item opens a screen or dialog that
//! a later lane owns. It holds no state, so it's an element, not an entity. Replaces HomeButton.svelte.

use gpui_kit::*;

#[derive(IntoElement)]
pub struct HomeButton;

impl RenderOnce for HomeButton {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
    }
}
