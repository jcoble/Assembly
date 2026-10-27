//! `LayoutStore` owns reading the saved layout at start and saving it when it changes, at most once a frame and with
//! no timer, so fast tab clicks save once. The layout goes through the backend's settings call over the socket (lane
//! g0-socket), never blocking the window. Replaces layoutStorage.ts and frame.ts's persistence (the
//! `shell.grid-layout` setting).

use gpui_kit::component::dock::DockAreaState;
use gpui_kit::*;

use super::frame::Frame;

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

    /// Reads the saved layout from the backend; `None` when there's none this build can read.
    pub fn load(&self, _cx: &mut Context<Frame>) -> Task<Option<DockAreaState>> {
        todo!()
    }

    /// Asks for the frame's layout to be saved on the next frame; more changes in the same frame add nothing.
    pub fn save_soon(&mut self, _window: &mut Window, _cx: &mut Context<Frame>) {
        todo!()
    }
}
