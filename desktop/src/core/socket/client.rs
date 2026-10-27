//! `SocketClient` is the window's handle on the socket, one per app (a GPUI global). Every feature calls and
//! subscribes through it; nothing else opens a socket. It emits each `ConnectionState` change.

use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{self, Sender};

use futures_channel::mpsc::unbounded;
use futures_channel::oneshot;

use gpui_kit::*;
use serde::de::DeserializeOwned;
use serde_json::Value;

use super::client_thread::ClientThread;
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
    pub fn start(endpoint: Endpoint, cx: &mut App) -> Entity<Self> {
        let (events, receiver) = mpsc::channel();
        let (states, mut changes) = unbounded();
        let next_id = Arc::new(AtomicU64::new(0));
        ClientThread::spawn(endpoint, receiver, events.clone(), states, next_id.clone());
        let client = cx.new(|_| Self {
            events,
            next_id,
            state: ConnectionState::Connecting,
        });
        let entity = client.clone();
        cx.spawn(async move |cx| {
            while let Ok(state) = changes.recv().await {
                entity.update(cx, |client, cx| {
                    client.state = state.clone();
                    cx.emit(state);
                    cx.notify();
                });
            }
        })
        .detach();
        cx.set_global(GlobalSocket(client.clone()));
        client
    }

    pub fn global(cx: &App) -> Entity<Self> {
        cx.global::<GlobalSocket>().0.clone()
    }

    pub fn state(&self) -> &ConnectionState {
        &self.state
    }

    /// One of a feature's calls. While `Disconnected` it fails at once. With `key`, a newer call with the same key answers this one `Aborted` at once and
    /// cancels it on the backend, so a fast switch never waits on a stale load. Keys are for loads.
    pub fn call<T: DeserializeOwned>(
        &self,
        feature: &str,
        method: &str,
        args: Value,
        key: Option<&str>,
    ) -> PendingCall<T> {
        let id = self.id();
        let (answer, outcome) = oneshot::channel();
        let (feature, method, key) = (feature.into(), method.into(), key.map(Into::into));
        let _ = self.events.send(Event::Call {
            id,
            feature,
            method,
            args,
            key,
            answer,
        });
        PendingCall::new(id, self.events.clone(), outcome)
    }

    /// A keyed topic's state, then each change. With no key, every change on the topic; with a key, only that key's.
    pub fn subscribe(&self, topic: &str, key: Option<&str>) -> TopicSubscription<Update> {
        let id = self.id();
        let (updates, receiver) = unbounded();
        let (topic, key) = (topic.into(), key.map(Into::into));
        let _ = self.events.send(Event::Subscribe {
            id,
            topic,
            key,
            updates,
        });
        TopicSubscription::new(id, self.events.clone(), receiver)
    }

    /// A session's rows after `after` (all of them with `None`), in order, none missed and none twice: a gap in
    /// `prevSeq` subscribes again from the last seq, and so does a reconnect. A jump comes as `RowUpdate::Jump`.
    pub fn subscribe_rows(
        &self,
        session_id: &str,
        after: Option<i64>,
    ) -> TopicSubscription<RowUpdate> {
        let id = self.id();
        let (rows, receiver) = unbounded();
        let session_id = session_id.into();
        let _ = self.events.send(Event::SubscribeRows {
            id,
            session_id,
            after,
            rows,
        });
        TopicSubscription::new(id, self.events.clone(), receiver)
    }

    /// Tries the socket again, at `endpoint`, when it's `Disconnected`; nothing otherwise. The launcher calls it on
    /// the backend's ready line (`listening on ...`), after restarting a backend that exited (whose token may be new),
    /// and when the Reconnect button finds the backend still running.
    pub fn reconnect(&self, endpoint: Endpoint) {
        let _ = self.events.send(Event::Reconnect(endpoint));
    }

    /// `reconnect` for the launcher's stdout thread, which isn't the window's: it only sends to the client thread.
    pub fn reconnector(&self) -> impl Fn(Endpoint) + Send + Sync + 'static {
        let events = self.events.clone();
        move |endpoint| {
            let _ = events.send(Event::Reconnect(endpoint));
        }
    }

    fn id(&self) -> u64 {
        self.next_id.fetch_add(1, Ordering::Relaxed) + 1
    }
}
