//! The client thread's rules against `FakeBackend`: hello, calls and their answers, cancel and latest-wins,
//! subscriptions, and a dropped socket's one reopen. Pins the draft list in docs/behavior/g0-socket.md.

use std::time::{Duration, Instant};

use futures_executor::block_on;
use serde_json::{Value, json};

use super::connection_state::ConnectionState;
use super::endpoint::Endpoint;
use super::error_code::ErrorCode;
use super::event::Event;
use super::fake_backend::{FakeBackend, Reply, hello};
use super::outcome::Outcome;
use super::pending_call::PendingCall;
use super::test_client::{
    DEADLINE, TestClient as Client, backend, connected, endpoint, error, outcome,
};
use super::update::Update;

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
fn a_refused_hello_is_shown_and_not_retried_until_reconnect() {
    let backend = backend();
    let wrong = Endpoint {
        port: backend.port,
        token: "wrong".into(),
    };
    let mut client = Client::start_with(wrong);
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), ConnectionState::Refused { code: 4401 });
    // Nothing tries again on its own, past the first wait of a dropped connection's retries.
    assert_eq!(client.state_within(Duration::from_secs(1)), None);
    // Only Reconnect (the launcher, with the token file read again) does.
    client
        .events
        .send(Event::Reconnect(endpoint(backend.port)))
        .unwrap();
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
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

// A keyed topic whose items are rows (the terminal's output, TerminalFeature.cs) gets each row as a change of its
// key, and the socket stays open.
#[test]
fn a_keyed_topic_of_rows_gets_each_row_as_a_change() {
    let backend = FakeBackend::start(|frame| {
        hello(frame).unwrap_or_else(|| match frame["t"].as_str() {
            Some("sub") => {
                let sub = frame["id"].clone();
                vec![
                    Reply::Frame(json!({"t": "state", "sub": sub, "seq": 0, "items": []})),
                    Reply::Frame(
                        json!({"t": "rows", "sub": sub, "row": {"seq": 2, "prevSeq": 0, "data": "aGk="}}),
                    ),
                ]
            }
            _ => vec![],
        })
    });
    let client = Client::start(backend.port);
    let (_, mut updates) = client.subscribe_key("terminal", Some("term-1"));
    assert!(
        matches!(block_on(updates.recv()).ok(), Some(Update::State(items)) if items.is_empty())
    );
    assert!(matches!(
        block_on(updates.recv()).ok(),
        Some(Update::Change { key, item }) if key == "term-1" && item["data"] == "aGk="
    ));
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
fn a_failed_reopen_is_disconnected_and_calls_fail_at_once() {
    let mut hellos = 0;
    let backend = FakeBackend::start(move |frame| {
        if frame["t"] == "hello" {
            hellos += 1;
            if hellos == 2 {
                return vec![Reply::Drop];
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
}

// A backend that welcomes and then drops each time gets one reopen at once, then the waits: no loop with no wait.
// Pins: g0-socket#7, g0-socket#8
#[test]
fn a_backend_that_drops_after_each_welcome_is_reopened_once_then_waited_on() {
    let backend = FakeBackend::start(|frame| {
        let mut replies = hello(frame).unwrap_or_default();
        replies.push(Reply::Drop);
        replies
    });
    let mut client = Client::start(backend.port);
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
    assert_eq!(client.state(), ConnectionState::Disconnected);
    // The first wait is 0.5 s.
    assert_eq!(client.state_within(Duration::from_millis(100)), None);
}

// A connection that worked (a frame came) gets the reopen at once again on its next drop.
// Pins: g0-socket#7
#[test]
fn a_connection_that_answered_gets_the_reopen_at_once_again() {
    let backend = backend();
    let mut client = Client::start(backend.port);
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
    drop(client.call("drop", None));
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
    let (_, echo) = client.call("echo", None);
    assert!(matches!(outcome(echo), Outcome::Ok(_)));
    drop(client.call("drop", None));
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
}

// Dropping the client ends its thread and its connection's reader (the test client joins the thread, with a
// deadline), and the backend sees the socket close.
#[test]
fn dropping_the_client_ends_its_threads_and_closes_the_socket() {
    let backend = backend();
    let mut client = Client::start(backend.port);
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
    drop(client);
    backend
        .closed
        .recv_timeout(DEADLINE)
        .expect("the socket closed within the deadline");
}

// A backend that stops reading can't hold the client thread: a write that can't finish within its bound ends the
// connection like a drop, and the client still stops when dropped.
#[test]
fn a_backend_that_stops_reading_ends_the_connection_and_the_client_still_stops() {
    let backend = FakeBackend::start(|frame| {
        hello(frame).unwrap_or_else(|| match frame["method"].as_str() {
            Some("stall") => vec![Reply::Stall],
            _ => vec![],
        })
    });
    let mut client = Client::start(backend.port);
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
    let _stall = client.call("stall", None);
    let big = "x".repeat(1 << 20);
    // The first few writes fill the socket's buffers in milliseconds; the one after blocks.
    let blocked = Instant::now();
    for id in 1000..1064 {
        let (answer, _) = futures_channel::oneshot::channel();
        let call = Event::Call {
            id,
            feature: "test".into(),
            method: "big".into(),
            args: json!({"big": big}),
            key: None,
            answer,
        };
        client.events.send(call).unwrap();
    }
    // A send has 5 s in all, however the kernel takes its bytes, so the connection ends 5 s after the send blocks.
    let ended = client.state_within(Duration::from_secs(15));
    assert_eq!(ended, Some(ConnectionState::Connecting));
    let took = blocked.elapsed();
    assert!(
        took < Duration::from_secs(8),
        "the connection ended after {took:?}"
    );
}

// The window's case: dropping the GPUI task that awaits a call, before its answer, sends that call's cancel, so the
// backend stops it (and answers "cancelled", which goes nowhere).
// Pins: g0-socket#4
#[gpui::test]
fn dropping_the_task_awaiting_a_call_cancels_it(cx: &mut gpui::TestAppContext) {
    let backend = backend();
    let client = Client::start(backend.port);
    let (id, answer) = client.call("slow", None);
    let call = PendingCall::<Value>::new(id, client.events.clone(), answer);
    let task = cx.executor().spawn(call);
    cx.run_until_parked();
    backend.next(); // hello
    assert_eq!(backend.next()["method"], "slow");
    // A dropped task's future is dropped when its executor next runs, as the app's does on its next tick.
    drop(task);
    cx.run_until_parked();
    assert_eq!(backend.next(), json!({"t": "cancel", "id": id}));
}

// Pins: g0-socket#4
#[test]
fn dropping_a_pending_call_before_its_answer_cancels_it() {
    let backend = backend();
    let client = Client::start(backend.port);
    let (id, answer) = client.call("slow", None);
    drop(PendingCall::<Value>::new(id, client.events.clone(), answer));
    backend.next(); // hello
    assert_eq!(backend.next()["method"], "slow");
    assert_eq!(backend.next(), json!({"t": "cancel", "id": id}));
}

// A frame this client can't read would come back on every reopen: it stops at Disconnected instead of looping.
#[test]
fn a_frame_that_does_not_read_is_disconnected_with_no_reopen() {
    let backend = FakeBackend::start(|frame| {
        hello(frame).unwrap_or_else(|| vec![Reply::Frame(json!({"t": "surprise"}))])
    });
    let mut client = Client::start(backend.port);
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
    let (_, waiting) = client.call("echo", None);
    assert!(matches!(outcome(waiting), Outcome::Aborted));
    assert_eq!(client.state(), ConnectionState::Disconnected);
}

// Pins: g0-socket#8
#[test]
fn a_disconnected_client_tries_again_on_its_own_after_a_short_wait() {
    let mut hellos = 0;
    let backend = FakeBackend::start(move |frame| {
        if frame["t"] == "hello" {
            hellos += 1;
            if hellos == 1 {
                return vec![Reply::Drop];
            }
        }
        hello(frame).unwrap_or_default()
    });
    let mut client = Client::start(backend.port);
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), ConnectionState::Disconnected);
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
}

// A 4400 after the welcome (a frame the backend doesn't take) won't change on a reopen: Refused, no retry.
#[test]
fn a_4400_after_the_welcome_is_refused_with_no_reopen() {
    let backend =
        FakeBackend::start(|frame| hello(frame).unwrap_or_else(|| vec![Reply::Close(4400)]));
    let mut client = Client::start(backend.port);
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
    let (_, waiting) = client.call("echo", None);
    assert!(matches!(outcome(waiting), Outcome::Aborted));
    assert_eq!(client.state(), ConnectionState::Refused { code: 4400 });
}

// Calls while Disconnected fail at once and don't put the next try off.
#[test]
fn calls_while_disconnected_do_not_postpone_the_next_try() {
    let mut hellos = 0;
    let backend = FakeBackend::start(move |frame| {
        if frame["t"] == "hello" {
            hellos += 1;
            if hellos == 1 {
                return vec![Reply::Drop];
            }
        }
        hello(frame).unwrap_or_default()
    });
    let mut client = Client::start(backend.port);
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), ConnectionState::Disconnected);
    let events = client.events.clone();
    std::thread::spawn(move || {
        for _ in 0..30 {
            let (answer, _) = futures_channel::oneshot::channel();
            let call = Event::Call {
                id: 0,
                feature: "t".into(),
                method: "m".into(),
                args: json!({}),
                key: None,
                answer,
            };
            if events.send(call).is_err() {
                return;
            }
            std::thread::sleep(Duration::from_millis(100));
        }
    });
    // The first wait is 0.5 s; the calls go on for 3 s.
    let soon = client.state_within(Duration::from_secs(2));
    assert_eq!(soon, Some(ConnectionState::Connecting));
    assert_eq!(client.state(), connected());
}

// The bulk socket's client is the same client on `/socket/bulk`: it reopens there after a drop, as the main one does.
#[test]
fn a_client_on_the_bulk_socket_reopens_it_after_a_drop() {
    let backend = backend();
    let mut client = Client::start_on(endpoint(backend.port), "/socket/bulk");
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
    drop(client.call("drop", None));
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), connected());
    let (_, echo) = client.call("echo", None);
    assert!(matches!(outcome(echo), Outcome::Ok(_)));
    let paths: Vec<(String, Value)> = backend.on.try_iter().collect();
    let hellos: Vec<&str> = paths
        .iter()
        .filter(|(_, frame)| frame["t"] == "hello")
        .map(|(path, _)| path.as_str())
        .collect();
    assert_eq!(hellos, ["/socket/bulk", "/socket/bulk"]);
}
