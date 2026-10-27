//! `LayoutStore` owns turning the saved layout into the one to open, and saving the layout when it changes: once per
//! update, however many changes the update made, with no timer. The read and the write come from outside: the
//! backend's `preferences` topic and `preferences/set` call (main.rs), the tests' own in tests. Replaces
//! layoutStorage.ts and frame.ts's persistence.

use std::cell::Cell;
use std::rc::Rc;

use futures_channel::oneshot;
use gpui_kit::component::dock::DockAreaState;
use gpui_kit::*;
use serde_json::Value;

use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::update::Update;

use super::default_layout::{LAYOUT_VERSION, default_state};
use super::frame::Frame;

/// The layout's key among the backend's preferences.
pub const LAYOUT_KEY: &str = "shell.dock-layout";

/// Saves a layout, until the returned task finishes. Dropping the task gives the save up.
pub type WriteLayout = Box<dyn Fn(Value, &mut App) -> Task<()>>;

pub struct LayoutStore {
    write: WriteLayout,
    /// The save asked for in this update, until it runs.
    save: Option<Task<()>>,
    /// The last save, while it's being written. A newer one replaces it, so the newest layout wins.
    writing: Option<Task<()>>,
}

/// The saved layout in the `preferences` topic's first update, its state. `None` when there's none, or the
/// subscription ended first.
pub fn saved_layout(first: Option<Update>) -> Option<Value> {
    let Some(Update::State(items)) = first else {
        return None;
    };
    items
        .into_iter()
        .find(|(key, _)| key == LAYOUT_KEY)
        .and_then(|(_, mut item)| item.get_mut("value").map(Value::take))
}

/// The saved layout in `first`, the `preferences` topic's first update, unless `connection` reports first that the
/// backend refused the client (a token or protocol it doesn't take). Then it's `None`, so the window opens the default
/// layout instead of waiting for a reconnect.
pub fn read_saved_layout<C: EventEmitter<ConnectionState>>(
    first: impl Future<Output = Option<Update>> + 'static,
    connection: &Entity<C>,
    cx: &mut App,
) -> Task<Option<Value>> {
    // Answered by whichever comes first, the update or the refusal.
    let (answer, answered) = oneshot::channel::<Option<Update>>();
    let answer = Rc::new(Cell::new(Some(answer)));
    let refused = answer.clone();
    let watch = cx.subscribe(connection, move |_, state, _| {
        if let ConnectionState::Refused { .. } = state
            && let Some(answer) = refused.take()
        {
            _ = answer.send(None);
        }
    });
    let reading = cx.spawn(async move |_| {
        let update = first.await;
        if let Some(answer) = answer.take() {
            _ = answer.send(update);
        }
    });
    cx.spawn(async move |_| {
        // Held until the answer, then dropped, which ends the subscription.
        let _held = (watch, reading);
        saved_layout(answered.await.ok().flatten())
    })
}

impl LayoutStore {
    pub fn new(write: WriteLayout) -> Self {
        Self {
            write,
            save: None,
            writing: None,
        }
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
                    Ok(layout) => frame.layout.writing = Some((frame.layout.write)(layout, cx)),
                    Err(error) => {
                        eprintln!("shell: the layout didn't serialize, so it wasn't saved: {error}")
                    }
                }
            });
        }));
    }
}
