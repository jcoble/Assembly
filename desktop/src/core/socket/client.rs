//! `SocketClient` is the window's handle on the socket, one per app (a GPUI global). Every feature calls and
//! subscribes through it; nothing else opens a socket. It emits each `ConnectionState` change.

use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{self, Sender};
use std::thread::JoinHandle;

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
    /// Brings the client thread's state changes to the entity.
    _states: Task<()>,
    /// Joined on drop, after `Event::Stop`.
    thread: Option<JoinHandle<()>>,
}

struct GlobalSocket(Entity<SocketClient>);

impl Global for GlobalSocket {}

impl Drop for SocketClient {
    /// Ends the client thread, which closes the socket and ends its reader, and waits for it.
    fn drop(&mut self) {
        let _ = self.events.send(Event::Stop);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

impl EventEmitter<ConnectionState> for SocketClient {}

impl SocketClient {
    /// Starts the client thread, which opens the socket at once, and makes the client the app's global.
    pub fn start(endpoint: Endpoint, cx: &mut App) -> Entity<Self> {
        let (events, receiver) = mpsc::channel();
        let (states, mut changes) = unbounded();
        let next_id = Arc::new(AtomicU64::new(0));
        let thread =
            ClientThread::spawn(endpoint, receiver, events.clone(), states, next_id.clone());
        let client = cx.new(|cx| {
            let states = cx.spawn(async move |this, cx| {
                while let Ok(state) = changes.recv().await {
                    let changed = this.update(cx, |client: &mut Self, cx| {
                        client.state = state.clone();
                        cx.emit(state);
                        cx.notify();
                    });
                    if changed.is_err() {
                        return;
                    }
                }
            });
            Self {
                events,
                next_id,
                state: ConnectionState::Connecting,
                _states: states,
                thread: Some(thread),
            }
        });
        cx.set_global(GlobalSocket(client.clone()));
        client
    }

    /// Starts the client for `/socket/bulk`, the backend's socket for the large calls.
    pub fn start_bulk(endpoint: Endpoint, cx: &mut App) -> Entity<Self> {
        Self::start(endpoint, cx)
    }

    pub fn global(cx: &App) -> Entity<Self> {
        cx.global::<GlobalSocket>().0.clone()
    }

    /// The client, once the backend's first ready line started it.
    pub fn try_global(cx: &App) -> Option<Entity<Self>> {
        cx.try_global::<GlobalSocket>()
            .map(|socket| socket.0.clone())
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

    /// A session's rows after `after`, in order, none missed and none twice: a gap in `prevSeq` subscribes again from
    /// the last seq, and so does a reconnect. With no `after`, or one too far behind, it starts with a
    /// `RowUpdate::Jump` to the last seq, and history comes from `rows/page`.
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

    /// Tries the socket again, at `endpoint`, when it's `Disconnected` or `Refused`; nothing otherwise. The launcher calls it, on
    /// the main thread, for each ready line (`listening on ...`) after the first, which starts the client: after
    /// restarting a backend that exited (whose token may be new), and when the Reconnect button finds the backend
    /// still running.
    pub fn reconnect(&self, endpoint: Endpoint) {
        let _ = self.events.send(Event::Reconnect(endpoint));
    }

    fn id(&self) -> u64 {
        self.next_id.fetch_add(1, Ordering::Relaxed) + 1
    }
}
