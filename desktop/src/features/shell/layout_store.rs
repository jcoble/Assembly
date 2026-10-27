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
    /// The save asked for in this update, until it runs.
    save: Option<Task<()>>,
}

impl LayoutStore {
    pub fn new(write: WriteLayout) -> Self {
        Self { write, save: None }
    }

    /// The saved layout when this build can read it, otherwise the default.
    pub fn layout_to_open(saved: Option<Value>) -> DockAreaState {
        let Some(value) = saved else {
            return default_state();
        };
        match serde_json::from_value::<DockAreaState>(value) {
            Ok(state) if state.version == Some(LAYOUT_VERSION) => state,
            Ok(state) => {
                eprintln!(
                    "shell: the saved layout is version {:?}, not {LAYOUT_VERSION}, so the default opens",
                    state.version
                );
                default_state()
            }
            Err(error) => {
                eprintln!("shell: the saved layout didn't read, so the default opens: {error}");
                default_state()
            }
        }
    }

    /// Saves the frame's layout once this update has settled, so the changes that follow from it (a tab click reaches
    /// the dock later in the same update) save with it; more changes before then add nothing.
    pub fn save_soon(&mut self, cx: &mut Context<Frame>) {
        if self.save.is_some() {
            return;
        }
        self.save = Some(cx.spawn(async move |frame, cx| {
            _ = frame.update(cx, |frame, cx| {
                frame.layout.save = None;
                let state = frame.dock().read(cx).dump(cx);
                match serde_json::to_value(state) {
                    Ok(layout) => (frame.layout.write)(layout, cx),
                    Err(error) => {
                        eprintln!("shell: the layout didn't serialize, so it wasn't saved: {error}")
                    }
                }
            });
        }));
    }
}
