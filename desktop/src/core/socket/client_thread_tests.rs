//! The client thread's rules against `FakeBackend`: hello, calls and their answers, cancel and latest-wins,
//! subscriptions, the rows gap rule, and a dropped socket's one reopen. Pins the draft list in
//! docs/behavior/g0-socket.md. Every wait is on the event that marks it done; nothing sleeps or polls.

use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{self, Sender};

use futures_channel::mpsc::{UnboundedReceiver, unbounded};
use futures_channel::oneshot;
use futures_executor::block_on;
use serde_json::{Value, json};

use super::client_thread::ClientThread;
use super::connection_state::ConnectionState;
use super::endpoint::Endpoint;
use super::error_code::ErrorCode;
use super::event::Event;
use super::fake_backend::{FakeBackend, Reply, hello};
use super::outcome::Outcome;
use super::row_update::RowUpdate;
use super::update::Update;

struct Client {
    events: Sender<Event>,
    states: UnboundedReceiver<ConnectionState>,
    ids: Arc<AtomicU64>,
}

impl Client {
    fn start(port: u16) -> Self {
        let (events, receiver) = mpsc::channel();
        let (states_tx, states) = unbounded();
        let ids = Arc::new(AtomicU64::new(0));
        ClientThread::spawn(
            endpoint(port),
            receiver,
            events.clone(),
            states_tx,
            ids.clone(),
        );
        Self {
            events,
            states,
            ids,
        }
    }

    fn id(&self) -> u64 {
        self.ids.fetch_add(1, Ordering::Relaxed) + 1
    }

    fn state(&mut self) -> ConnectionState {
        block_on(self.states.recv()).expect("a state")
    }

    fn call(&self, method: &str, key: Option<&str>) -> (u64, oneshot::Receiver<Outcome<Value>>) {
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

    fn subscribe(&self, topic: &str) -> (u64, UnboundedReceiver<Update>) {
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

    fn rows(&self, after: Option<i64>) -> UnboundedReceiver<RowUpdate> {
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

fn endpoint(port: u16) -> Endpoint {
    Endpoint {
        port,
        token: "secret".into(),
    }
}

fn connected() -> ConnectionState {
    ConnectionState::Connected {
        machine: "box".into(),
    }
}

fn outcome(receiver: oneshot::Receiver<Outcome<Value>>) -> Outcome<Value> {
    block_on(receiver).expect("an outcome")
}

fn row(seq: i64, prev: i64) -> Value {
    json!({"sessionId": "s1", "seq": seq, "runId": null, "itemId": null, "kind": "text", "payload": {},
        "createdAt": 0, "prevSeq": prev})
}

/// Answers each call by its method: `echo` with its id, `fail`, `cancelled` and `unreachable` with that error, `slow`
/// not at all, `drop` by dropping the socket.
fn backend() -> FakeBackend {
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

fn error(id: Value, code: &str, machine: Option<&str>) -> Vec<Reply> {
    vec![Reply::Frame(
        json!({"t": "error", "id": id, "code": code, "message": "no", "machine": machine}),
    )]
}

// Pins: g0-socket#1
#[test]
fn hello_carries_the_token_and_the_welcome_names_the_machine() {
    let backend = backend();
    let mut client = Client::start(backend.port);
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
    assert_eq!(
        backend.next(),
        json!({"t": "hello", "token": "secret", "protocol": 1})
    );
}

// Pins: g0-socket#1, g0-socket#8
#[test]
fn a_refused_hello_is_disconnected_with_no_retry() {
    let backend = backend();
    let (events, receiver) = mpsc::channel();
    let (states_tx, mut states) = unbounded();
    let wrong = Endpoint {
        port: backend.port,
        token: "wrong".into(),
    };
    ClientThread::spawn(
        wrong,
        receiver,
        events.clone(),
        states_tx,
        Arc::new(AtomicU64::new(0)),
    );
    assert_eq!(
        block_on(states.recv()).ok(),
        Some(ConnectionState::Connecting)
    );
    assert_eq!(
        block_on(states.recv()).ok(),
        Some(ConnectionState::Disconnected)
    );
    // A retry would show Connecting before Reconnect's.
    events
        .send(Event::Reconnect(endpoint(backend.port)))
        .unwrap();
    assert_eq!(
        block_on(states.recv()).ok(),
        Some(ConnectionState::Connecting)
    );
    assert_eq!(block_on(states.recv()).ok(), Some(connected()));
}

// Pins: g0-socket#2, g0-socket#6
#[test]
fn a_call_gets_its_result_or_its_error() {
    let backend = backend();
    let client = Client::start(backend.port);
    let (id, echo) = client.call("echo", None);
    let (_, fail) = client.call("fail", None);
    let (_, unreachable) = client.call("unreachable", None);
    assert!(matches!(outcome(echo), Outcome::Ok(result) if result == json!(id)));
    assert!(matches!(outcome(fail), Outcome::Failed(f) if f.code == ErrorCode::NotFound));
    let far = ErrorCode::Unreachable {
        machine: "far".into(),
    };
    assert!(matches!(outcome(unreachable), Outcome::Failed(f) if f.code == far));
}

// Pins: g0-socket#4
#[test]
fn a_cancelled_call_sends_cancel_and_settles_aborted() {
    let backend = backend();
    let client = Client::start(backend.port);
    let (id, slow) = client.call("slow", None);
    client.events.send(Event::Cancel { id }).unwrap();
    assert!(matches!(outcome(slow), Outcome::Aborted));
    backend.next(); // hello
    assert_eq!(backend.next()["method"], "slow");
    assert_eq!(backend.next(), json!({"t": "cancel", "id": id}));
}

// Pins: g0-socket#3
#[test]
fn a_newer_call_with_the_same_key_aborts_the_older_at_once() {
    let backend = backend();
    let client = Client::start(backend.port);
    let (older_id, older) = client.call("slow", Some("load"));
    let (newer_id, newer) = client.call("echo", Some("load"));
    assert!(matches!(outcome(older), Outcome::Aborted));
    assert!(matches!(outcome(newer), Outcome::Ok(result) if result == json!(newer_id)));
    backend.next(); // hello
    backend.next(); // the older call
    assert_eq!(backend.next(), json!({"t": "cancel", "id": older_id}));
}

// Pins: g0-socket#5
#[test]
fn a_subscription_gets_its_state_then_each_change_until_unsub() {
    let backend = FakeBackend::start(|frame| {
        hello(frame).unwrap_or_else(|| match frame["t"].as_str() {
            Some("sub") if frame["topic"] == "sessions" => {
                let sub = frame["id"].clone();
                vec![
                    Reply::Frame(
                        json!({"t": "state", "sub": sub, "items": [{"key": "a", "item": 1}]}),
                    ),
                    Reply::Frame(
                        json!({"t": "change", "sub": sub, "op": "upsert", "key": "b", "item": 2}),
                    ),
                ]
            }
            Some("sub") => error(frame["id"].clone(), "not_found", None),
            _ => vec![],
        })
    });
    let client = Client::start(backend.port);
    let (id, mut updates) = client.subscribe("sessions");
    assert!(
        matches!(block_on(updates.recv()).ok(), Some(Update::State(items)) if items == [("a".into(), json!(1))])
    );
    assert!(
        matches!(block_on(updates.recv()).ok(), Some(Update::Change { key, item }) if key == "b" && item == json!(2))
    );
    client.events.send(Event::Unsubscribe { id }).unwrap();
    backend.next(); // hello
    assert_eq!(backend.next()["t"], "sub");
    assert_eq!(backend.next(), json!({"t": "unsub", "id": id}));
    // A refused subscription ends its updates.
    let (_, mut missing) = client.subscribe("nope");
    assert!(block_on(missing.recv()).is_err());
}

// Pins: g0-socket#7
#[test]
fn a_drop_aborts_waiting_calls_reopens_once_and_subscribes_again() {
    let backend = backend();
    let mut client = Client::start(backend.port);
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
    let (sub, _updates) = client.subscribe("sessions");
    let (_, slow) = client.call("slow", None);
    let (_, dropped) = client.call("drop", None);
    assert!(matches!(outcome(slow), Outcome::Aborted));
    assert!(matches!(outcome(dropped), Outcome::Aborted));
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
    let frames: Vec<Value> = backend.seen.iter().take(6).collect();
    assert_eq!(frames[4]["t"], "hello");
    assert_eq!(
        frames[5],
        json!({"t": "sub", "id": sub, "topic": "sessions"})
    );
    // The calls the drop aborted aren't sent again.
    let (id, echo) = client.call("echo", None);
    assert!(matches!(outcome(echo), Outcome::Ok(_)));
    assert_eq!(backend.next()["id"], id);
}

// Pins: g0-socket#8
#[test]
fn a_failed_reopen_is_disconnected_until_reconnect_and_calls_fail_at_once() {
    let mut hellos = 0;
    let backend = FakeBackend::start(move |frame| {
        if frame["t"] == "hello" {
            hellos += 1;
            if hellos == 2 {
                return vec![Reply::Close(4401)];
            }
        }
        hello(frame).unwrap_or_else(|| vec![Reply::Drop])
    });
    let mut client = Client::start(backend.port);
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
    drop(client.call("drop", None));
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), ConnectionState::Disconnected);
    let (_, failed) = client.call("echo", None);
    assert!(matches!(outcome(failed), Outcome::Failed(f) if f.code == ErrorCode::Failed));
    client
        .events
        .send(Event::Reconnect(endpoint(backend.port)))
        .unwrap();
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
}

// Pins: g0-socket#9
#[test]
fn rows_pass_in_order_skip_repeats_and_resubscribe_after_the_last_seq_on_a_gap() {
    let mut subs = 0;
    let backend = FakeBackend::start(move |frame| {
        hello(frame).unwrap_or_else(|| {
            if frame["t"] != "sub" {
                return vec![];
            }
            subs += 1;
            let sub = frame["id"].clone();
            let state =
                |seq: i64| Reply::Frame(json!({"t": "state", "sub": sub, "seq": seq, "items": []}));
            let rows = |r: Value| Reply::Frame(json!({"t": "rows", "sub": sub, "row": r}));
            match subs {
                // After 3: 4 passes, 4 again is skipped, 6 after 4 is a gap.
                1 => vec![state(3), rows(row(4, 3)), rows(row(4, 3)), rows(row(6, 5))],
                // After 4: the missed 5, then 6.
                _ => vec![state(4), rows(row(5, 4)), rows(row(6, 5))],
            }
        })
    });
    let client = Client::start(backend.port);
    let mut rows = client.rows(Some(3));
    let mut seqs = vec![];
    while seqs.len() < 3 {
        match block_on(rows.recv()).ok() {
            Some(RowUpdate::Row(row)) => seqs.push(row.seq),
            other => panic!("expected a row, got {other:?}"),
        }
    }
    assert_eq!(seqs, [4, 5, 6]);
    backend.next(); // hello
    let first = backend.next();
    assert_eq!(
        (
            first["topic"].clone(),
            first["key"].clone(),
            first["after"].clone()
        ),
        (json!("rows"), json!("s1"), json!(3))
    );
    assert_eq!(backend.next(), json!({"t": "unsub", "id": first["id"]}));
    let again = backend.next();
    assert_eq!(
        (again["t"].clone(), again["after"].clone()),
        (json!("sub"), json!(4))
    );
    assert_ne!(again["id"], first["id"]);
}

// Pins: g0-socket#9
#[test]
fn a_rows_state_past_the_after_sent_is_a_jump() {
    let backend = FakeBackend::start(|frame| {
        hello(frame).unwrap_or_else(|| match frame["t"].as_str() {
            Some("sub") => {
                let sub = frame["id"].clone();
                vec![
                    Reply::Frame(json!({"t": "state", "sub": sub, "seq": 900, "items": []})),
                    Reply::Frame(json!({"t": "rows", "sub": sub, "row": row(901, 900)})),
                ]
            }
            _ => vec![],
        })
    });
    let client = Client::start(backend.port);
    let mut rows = client.rows(Some(3));
    assert!(matches!(
        block_on(rows.recv()).ok(),
        Some(RowUpdate::Jump { seq: 900 })
    ));
    assert!(matches!(block_on(rows.recv()).ok(), Some(RowUpdate::Row(row)) if row.seq == 901));
}

// Only the client thread writes: a ping in the middle of a burst is answered, and every frame around it arrives.
#[test]
fn a_ping_mid_burst_is_answered_and_no_frame_is_lost() {
    let backend = FakeBackend::start(|frame| {
        hello(frame).unwrap_or_else(|| match frame["t"].as_str() {
            Some("sub") => {
                let sub = frame["id"].clone();
                let change = |n: i64| {
                    Reply::Frame(
                        json!({"t": "change", "sub": sub, "key": n.to_string(), "item": n}),
                    )
                };
                let mut replies =
                    vec![Reply::Frame(json!({"t": "state", "sub": sub, "items": []}))];
                replies.extend((0..50).map(change));
                replies.push(Reply::Ping);
                replies.extend((50..100).map(change));
                replies
            }
            _ => vec![],
        })
    });
    let client = Client::start(backend.port);
    let (_, mut updates) = client.subscribe("burst");
    assert!(matches!(
        block_on(updates.recv()).ok(),
        Some(Update::State(_))
    ));
    for n in 0..100 {
        assert!(
            matches!(block_on(updates.recv()).ok(), Some(Update::Change { item, .. }) if item == json!(n))
        );
    }
    backend.next(); // hello
    backend.next(); // sub
    assert_eq!(backend.next(), json!({"t": "pong"}));
}
