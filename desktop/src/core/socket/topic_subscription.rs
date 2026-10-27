//! `TopicSubscription` is one subscription the window holds: dropping it sends `unsub`. `U` is `Update` for a keyed
//! topic and `RowUpdate` for the `rows` topic. The entity that owns it reads `updates` on the foreground, one small update
//! per changed item. It stays subscribed across reconnects. If the backend
//! refuses the `sub` (an unknown topic, a missing key), the failure is logged and `updates` ends.

use std::sync::mpsc::Sender;

use futures_channel::mpsc::UnboundedReceiver;

use super::event::Event;

pub struct TopicSubscription<U> {
    id: u64,
    events: Sender<Event>,
    pub updates: UnboundedReceiver<U>,
}

impl<U> Drop for TopicSubscription<U> {
    fn drop(&mut self) {
        todo!()
    }
}
