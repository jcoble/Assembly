//! `TestClient` drives a `ClientThread` through its events, for the tests against `FakeBackend`, and the fake
//! backend's usual answers. Every wait is on the event that marks it done, with a deadline so a missed event fails
//! the test instead of hanging it; nothing polls.

use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{self, Receiver, Sender};
use std::thread;
use std::time::Duration;

use futures_channel::mpsc::{UnboundedReceiver, unbounded};
use futures_channel::oneshot;
use futures_executor::block_on;
use serde_json::{Value, json};

use super::client_thread::ClientThread;
use super::connection_state::ConnectionState;
use super::endpoint::Endpoint;
use super::event::Event;
use super::fake_backend::{FakeBackend, Reply, hello};
use super::outcome::Outcome;
use super::row_update::RowUpdate;
use super::update::Update;

/// How long a test waits for an event it expects.
pub(super) const DEADLINE: Duration = Duration::from_secs(5);

pub(super) struct TestClient {
    pub events: Sender<Event>,
    states: Receiver<ConnectionState>,
    ids: Arc<AtomicU64>,
}

impl TestClient {
    pub fn start(port: u16) -> Self {
        Self::start_with(endpoint(port))
    }

    pub fn start_with(endpoint: Endpoint) -> Self {
        let (events, receiver) = mpsc::channel();
        let (states_tx, mut changes) = unbounded();
        let ids = Arc::new(AtomicU64::new(0));
        ClientThread::spawn(endpoint, receiver, events.clone(), states_tx, ids.clone());
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
        }
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
        let (answer, outcome) = oneshot::channel();
        let id = self.id();
        let (feature, method, key) = ("test".into(), method.into(), key.map(Into::into));
        self.events
            .send(Event::Call {
                id,
                feature,
                method,
                args: json!({}),
                key,
                answer,
            })
            .unwrap();
        (id, outcome)
    }

    pub fn subscribe(&self, topic: &str) -> (u64, UnboundedReceiver<Update>) {
        let (updates, receiver) = unbounded();
        let id = self.id();
        self.events
            .send(Event::Subscribe {
                id,
                topic: topic.into(),
                key: None,
                updates,
            })
            .unwrap();
        (id, receiver)
    }

    pub fn rows(&self, after: Option<i64>) -> UnboundedReceiver<RowUpdate> {
        let (rows, receiver) = unbounded();
        let id = self.id();
        self.events
            .send(Event::SubscribeRows {
                id,
                session_id: "s1".into(),
                after,
                rows,
            })
            .unwrap();
        receiver
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
