//! The client against the real desktop backend (`RealBackend`): hello and refusal, calls, topics, the drop and
//! reconnect. Each test starts its own backend. Pins the draft list in docs/behavior/g0-socket.md.

use std::fs;
use std::process::Command;
use std::sync::mpsc::Receiver;
use std::time::Duration;

use serde_json::json;

use super::connection_state::ConnectionState;
use super::endpoint::Endpoint;
use super::error_code::ErrorCode;
use super::event::Event;
use super::outcome::Outcome;
use super::real_backend::{RealBackend, folder};
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

// Pins: g0-socket#2
#[test]
fn a_call_gets_its_result_or_not_found() {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    let (client, _) = connect(&backend);
    let (_, agents) = client.call_to("machines", "agents", None);
    let Outcome::Ok(list) = outcome(agents) else {
        panic!("machines/agents failed");
    };
    assert_eq!(list["agents"], json!([]));
    let (_, missing) = client.call_to("machines", "nope", None);
    assert!(matches!(outcome(missing), Outcome::Failed(f) if f.code == ErrorCode::NotFound));
}

// Pins: g0-socket#5, g0-socket#14
#[test]
fn the_machines_topic_starts_with_this_machine_reachable() {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    let (client, _) = connect(&backend);
    let (_, machines) = client.subscribe("machines");
    let machines = updates(machines);
    let Update::State(items) = update(&machines) else {
        panic!("expected the machines state first");
    };
    let local = items.iter().find(|(key, _)| key == "local");
    let (_, local) = local.expect("this machine, keyed local");
    assert_eq!(local["name"], json!(null));
    assert_eq!(local["reachable"], json!(true));
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

// A start that fails (here a stand-in that never prints its ready line) stops the child and removes its folder.
#[test]
fn a_backend_that_never_gets_ready_is_stopped_and_its_folder_removed() {
    let backend = folder();
    let pid_file = folder().join("pid");
    let mut never_ready = Command::new("sh");
    never_ready
        .arg("-c")
        .arg("echo $$ > \"$0\"; exec sleep 60")
        .arg(&pid_file);
    let started = RealBackend::spawn(never_ready, backend.clone(), Duration::from_secs(1));
    assert!(started.is_err());
    assert!(!backend.exists());
    let pid = fs::read_to_string(&pid_file).expect("the stand-in's pid");
    let alive = Command::new("kill")
        .args(["-0", pid.trim()])
        .status()
        .expect("run kill");
    assert!(!alive.success(), "the stand-in is still running");
    let _ = fs::remove_dir_all(pid_file.parent().expect("its folder"));
}
