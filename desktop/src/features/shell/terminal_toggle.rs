//! Opening the Terminal, from the top row's button and the "..." menu. Stub until Task 4.

use gpui_kit::component::dock::{DockArea, DockPlacement};
use gpui_kit::*;

/// What pressing the Terminal's button or menu item does.
#[derive(Debug, PartialEq)]
#[allow(dead_code)]
pub enum TerminalPress {
    Open(DockPlacement),
    Show(DockPlacement),
    Shut(DockPlacement),
    Nothing,
}

/// What a press does, from where the Terminal is, whether its dock is open and whether it's showing.
#[allow(dead_code)]
pub fn press_terminal(_placement: DockPlacement, _dock_open: bool, _shown: bool) -> TerminalPress {
    TerminalPress::Nothing
}

/// Whether the Terminal is showing, wherever it has been dragged.
#[allow(dead_code)]
pub fn terminal_showing(_dock: &Entity<DockArea>, _cx: &App) -> bool {
    false
}

#[allow(dead_code)]
pub fn toggle_terminal(_dock: &Entity<DockArea>, _window: &mut Window, _cx: &mut App) {}

#[allow(dead_code)]
pub fn button(_dock: Entity<DockArea>, _cx: &App) -> impl IntoElement {
    div()
}
