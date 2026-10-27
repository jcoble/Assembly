//! `LayoutStore` owns reading the saved layout at start (the default when it's missing or unreadable) and saving it
//! when it changes, at most once a frame and with no timer, so fast tab clicks save once. The layout is saved through
//! the backend's settings call over the socket (lane g0-socket). Replaces layoutStorage.ts and frame.ts's
//! persistence (the `shell.grid-layout` setting).

use gpui_kit::component::dock::{DockArea, DockAreaState};
use gpui_kit::*;

/// The setting the layout is saved under.
pub const LAYOUT_SETTING: &str = "shell.dock-layout";

pub struct LayoutStore {
    /// A save is already asked for this frame.
    save_pending: bool,
}

impl LayoutStore {
    pub fn new() -> Self {
        todo!()
    }

    /// The saved layout, if there is one this build can read.
    pub fn load(&self, _cx: &App) -> Option<DockAreaState> {
        todo!()
    }

    /// Asks for the layout to be saved on the next frame; more changes in the same frame add nothing.
    pub fn save_soon(&mut self, _dock: &Entity<DockArea>, _window: &mut Window, _cx: &mut App) {
        todo!()
    }
}
