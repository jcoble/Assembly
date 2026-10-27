//! The client against the real desktop backend (`RealBackend`): hello and refusal, calls, topics, the drop and
//! reconnect. Each test starts its own backend. Pins the draft list in docs/behavior/g0-socket.md.

use std::sync::mpsc::Receiver;

use serde_json::json;

use super::connection_state::ConnectionState;
use super::endpoint::Endpoint;
use super::error_code::ErrorCode;
use super::event::Event;
use super::outcome::Outcome;
use super::real_backend::RealBackend;
use super::test_client::{DEADLINE, TestClient as Client, outcome, updates};
use super::update::Update;

/// A client connected to `backend`; the machine it names.
fn connect(backend: &RealBackend) -> (Client, String) {
    let mut client = Client::start_with(backend.endpoint());
    assert_eq!(client.state(), ConnectionState::Connecting);
    match client.state() {
        ConnectionState::Connected { machine } => (client, machine),
        other => panic!("expected Connected, got {other:?}"),
    }
}

fn update(updates: &Receiver<Update>) -> Update {
    updates
        .recv_timeout(DEADLINE)
        .expect("an update within the deadline")
}

// Pins: g0-socket#1
#[test]
fn the_backend_welcomes_the_token_and_names_its_machine() {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    let (_client, machine) = connect(&backend);
    assert!(!machine.is_empty());
}

// Pins: g0-socket#1, g0-socket#8
#[test]
fn a_wrong_token_is_refused_with_4401() {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    let mut client = Client::start_with(Endpoint {
        port: backend.port,
        token: "wrong".into(),
    });
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), ConnectionState::Refused { code: 4401 });
}

// Pins: g0-socket#2, g0-socket#14
#[test]
fn a_call_gets_its_result_or_not_found() {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    let (client, _) = connect(&backend);
    let (_, machines) = client.call_to("machines", "list", None);
    let Outcome::Ok(list) = outcome(machines) else {
        panic!("machines/list failed");
    };
    assert_eq!(list["machines"][0]["reachable"], json!(true));
    let (_, missing) = client.call_to("machines", "nope", None);
    assert!(matches!(outcome(missing), Outcome::Failed(f) if f.code == ErrorCode::NotFound));
}

// Pins: g0-socket#5, g0-socket#15
#[test]
fn the_clock_topic_gives_its_state_then_ticks() {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    let (client, _) = connect(&backend);
    let (_, clock) = client.subscribe_key("clock", Some("second"));
    let clock = updates(clock);
    assert!(matches!(update(&clock), Update::State(_)));
    assert!(matches!(update(&clock), Update::Change { item, .. } if item["now"].is_i64()));
}

// Pins: g0-socket#7
#[test]
fn a_drop_reconnects_at_once_and_subscribes_again() {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    let (mut client, machine) = connect(&backend);
    let (_, sessions) = client.subscribe("sessions");
    let sessions = updates(sessions);
    assert!(matches!(update(&sessions), Update::State(_)));
    let (_, dropped) = client.call_to("diagnostics", "drop", None);
    // The drop may answer before the socket ends, or end it first.
    assert!(matches!(
        outcome(dropped),
        Outcome::Ok(_) | Outcome::Aborted
    ));
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), ConnectionState::Connected { machine });
    assert!(matches!(update(&sessions), Update::State(_)));
}

// Pins: g0-socket#8
#[test]
fn a_stopped_backend_is_disconnected_until_reconnect_to_the_new_one() {
    let Some(old) = RealBackend::start() else {
        return;
    };
    let Some(new) = RealBackend::start() else {
        return;
    };
    let (mut client, _) = connect(&old);
    drop(old);
    assert_eq!(client.state(), ConnectionState::Connecting);
    assert_eq!(client.state(), ConnectionState::Disconnected);
    client
        .events
        .send(Event::Reconnect(new.endpoint()))
        .unwrap();
    // A try on its own, at the old port, may come first and fail; the reconnect to the new one connects.
    for _ in 0..6 {
        if matches!(client.state(), ConnectionState::Connected { .. }) {
            return;
        }
    }
    panic!("not Connected after Reconnect");
}
