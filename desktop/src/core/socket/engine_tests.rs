//! Providers stream behavior on the existing socket, without any actual provider operation.
use super::engine_subscription::{EnginePage, EngineSubscription};
use super::event::Event;
use super::fake_backend::{FakeBackend, Reply, hello};
use super::in_frame::InFrame;
use super::outcome::Outcome;
use super::test_client::{TestClient, outcome};
use futures_channel::mpsc::unbounded;
use futures_executor::block_on;
use serde_json::{Value, json};

fn subscription(client: &TestClient) -> EngineSubscription {
    let id = client.id();
    let (send, recv) = unbounded();
    client
        .events
        .send(Event::SubscribeEngine {
            id,
            topic: "providers".into(),
            machine: "owner".into(),
            updates: send,
        })
        .unwrap();
    EngineSubscription::new(id, client.events.clone(), recv)
}
fn fixture() -> FakeBackend {
    FakeBackend::start(|frame| {
        hello(frame).unwrap_or_else(|| match frame["t"].as_str() {
        Some("sub") => [true, false].into_iter().map(|more| Reply::Frame(json!({"t":"engine_snapshot","id":frame["id"],"machine":"owner","through":5,"items":[],"more":more}))).collect(),
        Some("call") => vec![Reply::Frame(json!({"t":"result","id":frame["id"],"result":null}))],
        _ => vec![],
    })
    })
}
fn incoming(client: &TestClient, frame: Value) {
    client
        .events
        .send(Event::Frame(
            serde_json::from_value::<InFrame>(frame).unwrap(),
        ))
        .unwrap();
}

#[test]
fn providers_pages_are_server_driven_and_only_applied_complete_pages_acknowledge() {
    let backend = fixture();
    let client = TestClient::start(backend.port);
    let mut sub = subscription(&client);
    let first = block_on(sub.updates.recv()).unwrap();
    assert!(matches!(
        &first.page,
        EnginePage::Snapshot {
            more: true,
            reset: true,
            ..
        }
    ));
    sub.acknowledge(&first);
    let second = block_on(sub.updates.recv()).unwrap();
    assert!(matches!(
        &second.page,
        EnginePage::Snapshot {
            more: false,
            reset: false,
            ..
        }
    ));
    sub.acknowledge(&second);
    assert_eq!(backend.next()["t"], "hello");
    assert_eq!(
        backend.next(),
        json!({"t":"sub","id":first.generation,"stream":"engine","topic":"providers","machine":"owner"})
    );
    assert_eq!(
        backend.next(),
        json!({"t":"ack","id":second.generation,"stream":"engine","after":5})
    );
    drop(sub);
    assert_eq!(backend.next(), json!({"t":"unsub","id":second.generation}));
}

#[test]
fn empty_events_advance_cursor_and_a_gap_resnapshots_without_accepting_old_ack() {
    let backend = fixture();
    let client = TestClient::start(backend.port);
    let mut sub = subscription(&client);
    block_on(sub.updates.recv()).unwrap();
    let initial = block_on(sub.updates.recv()).unwrap();
    let wire = initial.generation;
    incoming(
        &client,
        json!({"t":"engine_events","id":wire,"machine":"owner","after":5,"through":9,"events":[],"more":false}),
    );
    let changed = block_on(sub.updates.recv()).unwrap();
    assert!(matches!(
        &changed.page,
        EnginePage::Events {
            after: 5,
            through: 9,
            ..
        }
    ));
    sub.acknowledge(&changed);
    incoming(
        &client,
        json!({"t":"engine_events","id":wire,"machine":"owner","after":10,"through":11,"events":[],"more":false}),
    );
    let replacement = block_on(sub.updates.recv()).unwrap();
    assert_ne!(replacement.generation, wire);
    assert!(matches!(
        &replacement.page,
        EnginePage::Snapshot { reset: true, .. }
    ));
    sub.acknowledge(&changed);
    let (_, barrier) = client.call("barrier", None);
    assert!(matches!(outcome(barrier), Outcome::Ok(_)));
    let frames: Vec<_> = backend.seen.try_iter().collect();
    assert_eq!(frames.iter().filter(|f| f["t"] == "ack").count(), 1);
    assert_eq!(frames.iter().filter(|f| f["t"] == "sub").count(), 2);
    assert!(frames.iter().all(|f| f["t"] != "next"));
}

#[test]
fn fresh_wire_ids_keep_late_responses_out_of_the_successor() {
    let backend = fixture();
    let client = TestClient::start(backend.port);
    let mut sub = subscription(&client);
    let old = block_on(sub.updates.recv()).unwrap();
    block_on(sub.updates.recv()).unwrap();
    sub.resnapshot();
    let fresh = block_on(sub.updates.recv()).unwrap();
    block_on(sub.updates.recv()).unwrap();
    assert_ne!(old.generation, fresh.generation);
    incoming(
        &client,
        json!({"t":"engine_events","id":old.generation,"machine":"owner","after":5,"through":100,"events":[],"more":false}),
    );
    let (_, barrier) = client.call("barrier", None);
    assert!(matches!(outcome(barrier), Outcome::Ok(_)));
    assert!(sub.updates.try_recv().is_err());
    drop(sub);
    while {
        let f = backend.next();
        f["t"] != "unsub" || f["id"] != fresh.generation
    } {}
}

#[test]
fn engine_subscription_error_is_delivered_without_logging_server_material() {
    let backend = FakeBackend::start(|frame| {
        hello(frame).unwrap_or_else(|| {
            vec![Reply::Frame(
                json!({"t":"error","id":frame["id"],"code":"failed","message":"fixed safe error"}),
            )]
        })
    });
    let client = TestClient::start(backend.port);
    let mut sub = subscription(&client);
    assert!(matches!(
        block_on(sub.updates.recv()).unwrap().page,
        EnginePage::Unavailable
    ));
}
