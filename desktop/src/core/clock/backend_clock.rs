//! `BackendClock` is the window's subscriber to the backend's `clock` topic, key `second`. It's subscribed only while
//! something holds it (the meter while it's on, a debounced save while one is waiting), so nothing runs at rest, and
//! it emits a `Tick` for each second.

use std::time::Instant;

use gpui_kit::*;

use super::tick::Tick;
use crate::core::socket::client::SocketClient;
use crate::core::socket::update::Update;

pub struct BackendClock {
    /// How many holders want ticks; the subscription lives while it's above zero.
    holders: usize,
    /// Owns the subscription; dropping it unsubscribes.
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
    pub fn hold(&mut self, cx: &mut Context<Self>) {
        self.holders += 1;
        if self.feed.is_some() {
            return;
        }
        let mut subscription = SocketClient::global(cx)
            .read(cx)
            .subscribe("clock", Some("second"));
        self.feed = Some(cx.spawn(async move |this, cx| {
            while let Ok(update) = subscription.updates.recv().await {
                let item = match update {
                    Update::State(items) => items.into_iter().next().map(|(_, item)| item),
                    Update::Change { item, .. } => Some(item),
                };
                let Some(now) = item.as_ref().and_then(|item| item["now"].as_i64()) else {
                    continue;
                };
                let tick = Tick {
                    now,
                    received: Instant::now(),
                };
                if this.update(cx, |_, cx| cx.emit(tick)).is_err() {
                    return;
                }
            }
        }));
    }

    /// Ends one holder's ticks, unsubscribing after the last.
    pub fn release(&mut self, _cx: &mut Context<Self>) {
        self.holders = self.holders.saturating_sub(1);
        if self.holders == 0 {
            self.feed = None;
        }
    }
}
