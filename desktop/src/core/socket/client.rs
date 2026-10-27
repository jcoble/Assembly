//! `SocketClient` is the window's handle on the socket, one per app (a GPUI global). Every feature calls and
//! subscribes through it; nothing else opens a socket. It emits each `ConnectionState` change.

use std::sync::Arc;
use std::sync::atomic::AtomicU64;
use std::sync::mpsc::Sender;

use gpui_kit::*;
use serde::de::DeserializeOwned;
use serde_json::Value;

use super::connection_state::ConnectionState;
use super::endpoint::Endpoint;
use super::event::Event;
use super::pending_call::PendingCall;
use super::row_update::RowUpdate;
use super::topic_subscription::TopicSubscription;
use super::update::Update;

pub struct SocketClient {
    events: Sender<Event>,
    /// Call and subscription ids, shared by every caller so a handle knows its own id to cancel or unsub.
    next_id: Arc<AtomicU64>,
    state: ConnectionState,
}

struct GlobalSocket(Entity<SocketClient>);

impl Global for GlobalSocket {}

impl EventEmitter<ConnectionState> for SocketClient {}

impl SocketClient {
    /// Starts the client thread, which opens the socket at once, and makes the client the app's global.
    pub fn start(_endpoint: Endpoint, _cx: &mut App) -> Entity<Self> {
        todo!()
    }

    pub fn global(_cx: &App) -> Entity<Self> {
        todo!()
    }

    pub fn state(&self) -> &ConnectionState {
        &self.state
    }

    /// One of a feature's calls. While `Disconnected` it fails at once. With `key`, a newer call with the same key answers this one `Aborted` at once and
    /// cancels it on the backend, so a fast switch never waits on a stale load. Keys are for loads.
    pub fn call<T: DeserializeOwned>(
        &self,
        _feature: &str,
        _method: &str,
        _args: Value,
        _key: Option<&str>,
    ) -> PendingCall<T> {
        todo!()
    }

    /// A keyed topic's state, then each change. With no key, every change on the topic; with a key, only that key's.
    pub fn subscribe(&self, _topic: &str, _key: Option<&str>) -> TopicSubscription<Update> {
        todo!()
    }

    /// A session's rows after `after` (all of them with `None`), in order, none missed and none twice: a gap in
    /// `prevSeq` subscribes again from the last seq, and so does a reconnect. A jump comes as `RowUpdate::Jump`.
    pub fn subscribe_rows(
        &self,
        _session_id: &str,
        _after: Option<i64>,
    ) -> TopicSubscription<RowUpdate> {
        todo!()
    }

    /// Tries the socket again, at `endpoint`, when it's `Disconnected`; nothing otherwise. The launcher calls it on
    /// the backend's ready line (`listening on ...`), after restarting a backend that exited (whose token may be new),
    /// and when the Reconnect button finds the backend still running.
    pub fn reconnect(&self, _endpoint: Endpoint) {
        todo!()
    }
}
