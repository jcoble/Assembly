//! `LayoutStore` owns turning the saved layout into the one to open, and saving the layout when it changes: once per
//! update, however many changes the update made, with no timer. The read and the write come from outside: lane
//! g0-socket's preferences calls once it lands, the tests' own until then. Replaces layoutStorage.ts and frame.ts's
//! persistence.

use gpui_kit::component::dock::DockAreaState;
use gpui_kit::*;
use serde_json::Value;

use super::default_layout::{LAYOUT_VERSION, default_state};
use super::frame::Frame;

pub type WriteLayout = Box<dyn Fn(Value, &mut App)>;

pub struct LayoutStore {
    write: WriteLayout,
    /// A save is already asked for in this update.
    save_pending: bool,
}

impl LayoutStore {
    pub fn new(write: WriteLayout) -> Self {
        Self { write, save_pending: false }
    }

    /// The saved layout when this build can read it, otherwise the default.
    pub fn layout_to_open(saved: Option<Value>) -> DockAreaState {
        saved
            .and_then(|value| serde_json::from_value::<DockAreaState>(value).ok())
            .filter(|state| state.version == Some(LAYOUT_VERSION))
            .unwrap_or_else(default_state)
    }

    /// Saves the frame's layout at the end of this update; more changes in the same update add nothing.
    pub fn save_soon(&mut self, window: &mut Window, cx: &mut Context<Frame>) {
        if self.save_pending {
            return;
        }
        self.save_pending = true;
        cx.defer_in(window, |frame, _, cx| {
            frame.layout.save_pending = false;
            let state = frame.dock().read(cx).dump(cx);
            match serde_json::to_value(state) {
                Ok(layout) => (frame.layout.write)(layout, cx),
                Err(error) => eprintln!("shell: the layout didn't serialize, so it wasn't saved: {error}"),
            }
        });
    }
}
