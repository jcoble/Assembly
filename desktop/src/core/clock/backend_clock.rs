//! `BackendClock` is the window's subscriber to the backend's `clock` topic, key `second`. It's subscribed only while
//! something holds it (the meter while it's on, a debounced save while one is waiting), so nothing runs at rest, and
//! it emits a `Tick` for each second.

use gpui_kit::*;

use super::tick::Tick;

pub struct BackendClock {
    /// How many holders want ticks; the subscription lives while it's above zero.
    holders: usize,
    feed: Option<Task<()>>,
}

impl EventEmitter<Tick> for BackendClock {}

impl BackendClock {
    pub fn new() -> Self {
        Self {
            holders: 0,
            feed: None,
        }
    }

    /// Starts the ticks for one more holder, subscribing if it's the first.
    pub fn hold(&mut self, _cx: &mut Context<Self>) {
        todo!()
    }

    /// Ends one holder's ticks, unsubscribing after the last.
    pub fn release(&mut self, _cx: &mut Context<Self>) {
        todo!()
    }
}
