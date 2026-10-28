//! `TestClient` drives a `ClientThread` through its events, for the tests against `FakeBackend`, and the fake
//! backend's usual answers. Every wait is on the event that marks it done, with a deadline so a missed event fails
//! the test instead of hanging it; nothing polls.

use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{self, Receiver, Sender};
use std::thread::{self, JoinHandle};
use std::time::Duration;

use futures_channel::mpsc::{UnboundedReceiver, unbounded};
use futures_channel::oneshot;
use futures_executor::block_on;
use serde::de::DeserializeOwned;
use serde_json::{Value, json};

use super::client_thread::ClientThread;
use super::connection_state::ConnectionState;
use super::endpoint::Endpoint;
use super::event::Event;
use super::fake_backend::{FakeBackend, Reply, hello};
use super::outcome::Outcome;
use super::row_update::{RowUpdate, rows_sink};
use super::update::Update;

/// How long a test waits for an event it expects.
pub(super) const DEADLINE: Duration = Duration::from_secs(5);

pub(super) struct TestClient {
    pub events: Sender<Event>,
    states: Receiver<ConnectionState>,
    ids: Arc<AtomicU64>,
    thread: Option<JoinHandle<()>>,
}

impl TestClient {
    pub fn start(port: u16) -> Self {
        Self::start_with(endpoint(port))
    }

    /// A client on the socket at `path`, such as `/socket/bulk`.
    pub fn start_on(endpoint: Endpoint, path: &'static str) -> Self {
        let (events, receiver) = mpsc::channel();
        let (states_tx, mut changes) = unbounded();
        let ids = Arc::new(AtomicU64::new(0));
        let thread = ClientThread::spawn(
            endpoint,
            path,
            receiver,
            events.clone(),
            states_tx,
            ids.clone(),
        );
        // The state channel is async; a thread hands each state on to one the test can wait on with a deadline.
        let (forward, states) = mpsc::channel();
        thread::spawn(move || {
            while let Ok(state) = block_on(changes.recv()) {
                if forward.send(state).is_err() {
                    return;
                }
            }
        });
        Self {
            events,
            states,
            ids,
            thread: Some(thread),
        }
    }

    pub fn start_with(endpoint: Endpoint) -> Self {
        Self::start_on(endpoint, "/socket")
    }

    pub fn id(&self) -> u64 {
        self.ids.fetch_add(1, Ordering::Relaxed) + 1
    }

    pub fn state(&mut self) -> ConnectionState {
        self.states
            .recv_timeout(DEADLINE)
            .expect("a state change within the deadline")
    }

    /// The next state change within `wait`, if any.
    pub fn state_within(&mut self, wait: Duration) -> Option<ConnectionState> {
        self.states.recv_timeout(wait).ok()
    }

    pub fn call(
        &self,
        method: &str,
        key: Option<&str>,
    ) -> (u64, oneshot::Receiver<Outcome<Value>>) {
        self.call_to("test", method, key)
    }

    pub fn call_to(
        &self,
        feature: &str,
        method: &str,
        key: Option<&str>,
    ) -> (u64, oneshot::Receiver<Outcome<Value>>) {
        self.call_with(feature, method, json!({}), key)
    }

    pub fn call_with(
        &self,
        feature: &str,
        method: &str,
        args: Value,
        key: Option<&str>,
    ) -> (u64, oneshot::Receiver<Outcome<Value>>) {
        let (answer, outcome) = oneshot::channel();
        let id = self.id();
        let (feature, method, key) = (feature.into(), method.into(), key.map(Into::into));
        self.events
            .send(Event::Call {
                id,
                feature,
                method,
                args,
                key,
                answer,
            })
            .unwrap();
        (id, outcome)
    }

    pub fn subscribe(&self, topic: &str) -> (u64, UnboundedReceiver<Update>) {
        self.subscribe_key(topic, None)
    }

    pub fn subscribe_key(
        &self,
        topic: &str,
        key: Option<&str>,
    ) -> (u64, UnboundedReceiver<Update>) {
        let (updates, receiver) = unbounded();
        let id = self.id();
        self.events
            .send(Event::Subscribe {
                id,
                topic: topic.into(),
                key: key.map(Into::into),
                updates,
            })
            .unwrap();
        (id, receiver)
    }

    pub fn rows(&self, after: Option<i64>) -> UnboundedReceiver<RowUpdate> {
        self.rows_of("s1", after)
    }

    pub fn rows_of(&self, session: &str, after: Option<i64>) -> UnboundedReceiver<RowUpdate> {
        self.rows_on("rows", session, after)
    }

    /// Any rows topic's rows, by key, read into `R`.
    pub fn rows_on<R: DeserializeOwned + Send + 'static>(
        &self,
        topic: &str,
        key: &str,
        after: Option<i64>,
    ) -> UnboundedReceiver<RowUpdate<R>> {
        let (rows, receiver) = unbounded();
        let id = self.id();
        self.events
            .send(Event::SubscribeRows {
                id,
                topic: topic.into(),
                key: key.into(),
                after,
                rows: rows_sink(rows),
            })
            .unwrap();
        receiver
    }
}

/// Stops the client thread as `SocketClient` does, and fails the test if it doesn't end within the deadline.
impl Drop for TestClient {
    fn drop(&mut self) {
        let _ = self.events.send(Event::Stop);
        let Some(thread) = self.thread.take() else {
            return;
        };
        let (done, ended) = mpsc::channel();
        thread::spawn(move || {
            let _ = thread.join();
            let _ = done.send(());
        });
        if ended.recv_timeout(DEADLINE).is_err() && !thread::panicking() {
            panic!("the client thread didn't end within the deadline");
        }
    }
}

pub(super) fn endpoint(port: u16) -> Endpoint {
    Endpoint {
        port,
        token: "secret".into(),
    }
}

pub(super) fn connected() -> ConnectionState {
    ConnectionState::Connected {
        machine: "box".into(),
    }
}

pub(super) fn outcome(receiver: oneshot::Receiver<Outcome<Value>>) -> Outcome<Value> {
    let (forward, outcome) = mpsc::channel();
    thread::spawn(move || {
        let _ = forward.send(block_on(receiver));
    });
    let answered = outcome
        .recv_timeout(DEADLINE)
        .expect("an outcome within the deadline");
    answered.expect("an outcome")
}

/// Hands a subscription's updates on to a channel the test can wait on with a deadline.
pub(super) fn updates<T: Send + 'static>(mut receiver: UnboundedReceiver<T>) -> Receiver<T> {
    let (forward, updates) = mpsc::channel();
    thread::spawn(move || {
        while let Ok(update) = block_on(receiver.recv()) {
            if forward.send(update).is_err() {
                return;
            }
        }
    });
    updates
}

pub(super) fn row(seq: i64, prev: i64) -> Value {
    json!({"sessionId": "s1", "seq": seq, "runId": null, "itemId": null, "kind": "text", "payload": {},
        "createdAt": 0, "prevSeq": prev})
}

/// Answers each call by its method: `echo` with its id, `fail`, `cancelled` and `unreachable` with that error, `slow`
/// not at all, `drop` by dropping the socket.
pub(super) fn backend() -> FakeBackend {
    FakeBackend::start(|frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        let id = frame["id"].clone();
        match (frame["t"].as_str(), frame["method"].as_str()) {
            (Some("call"), Some("echo")) => {
                vec![Reply::Frame(json!({"t": "result", "id": id, "result": id}))]
            }
            (Some("call"), Some("fail")) => error(id, "not_found", None),
            (Some("call"), Some("unreachable")) => error(id, "unreachable", Some("far")),
            (Some("cancel"), _) => error(id, "cancelled", None),
            (Some("call"), Some("drop")) => vec![Reply::Drop],
            _ => vec![],
        }
    })
}

pub(super) fn error(id: Value, code: &str, machine: Option<&str>) -> Vec<Reply> {
    vec![Reply::Frame(
        json!({"t": "error", "id": id, "code": code, "message": "no", "machine": machine}),
    )]
}
