//! Scoped engine snapshots, reconnect replay and bulk-connection tests against a real local WebSocket.
use super::engine_update::EngineUpdate;
use super::event::Event;
use super::fake_backend::{FakeBackend, Reply, hello};
use super::test_client::DEADLINE;
use super::test_client::TestClient;
use futures_channel::mpsc::unbounded;
use futures_executor::block_on;
use serde_json::json;
use std::sync::mpsc;

#[test]
fn scoped_snapshot_publishes_only_the_complete_cut_and_unsubscribes_on_drop() {
    let backend = FakeBackend::start(|frame| {
        if let Some(welcome) = hello(frame) {
            return welcome;
        }
        if frame["t"] == "sub" {
            return vec![
                Reply::Frame(
                    json!({"t":"engine_snapshot","id":frame["id"],"machine":"owner","through":9,
                    "items":[{"entityKind":"session","id":"a","machine":"owner","version":1}],"more":true}),
                ),
                Reply::Frame(
                    json!({"t":"engine_snapshot","id":frame["id"],"machine":"owner","through":9,
                    "items":[{"entityKind":"session","id":"b","machine":"owner","version":2}],"more":false,
                    "at":{"kind":"session.changed","entityId":"b","version":2}}),
                ),
            ];
        }
        vec![]
    });
    let client = TestClient::start(backend.port);
    let id = client.id();
    let (updates, mut received) = unbounded();
    client
        .events
        .send(Event::SubscribeEngineScope {
            id,
            topic: "sessions".into(),
            key: None,
            machine: Some("owner".into()),
            updates,
        })
        .unwrap();
    assert_eq!(backend.next()["t"], "hello");
    let sub = backend.next();
    assert_eq!(sub["topic"], "sessions");
    assert!(sub.get("key").is_none());
    match block_on(received.recv()).unwrap() {
        EngineUpdate::Scoped(page) => {
            assert!(page.snapshot);
            assert_eq!(page.machine, "owner");
            assert_eq!(page.through, 9);
            assert_eq!(page.items.len(), 2);
            assert_eq!(page.items[0]["id"], "a");
            assert_eq!(page.items[1]["id"], "b");
            assert_eq!(page.at.as_ref().unwrap().entity_id, "b");
            client
                .events
                .send(Event::AcknowledgeEngine {
                    handle: id,
                    wire: page.wire,
                    through: page.through,
                    at: page.at,
                })
                .unwrap();
        }
        update => panic!("expected a complete scoped snapshot, got {update:?}"),
    }
    assert_eq!(
        backend.next(),
        json!({"t":"ack","id":id,"stream":"engine","after":9})
    );
    client.events.send(Event::Unsubscribe { id }).unwrap();
    assert_eq!(backend.next(), json!({"t":"unsub","id":id}));
}

#[test]
fn engine_identity_is_cleared_until_the_reconnected_welcome_names_its_owner() {
    let (allow, ready) = mpsc::channel();
    let (entered, reconnecting) = mpsc::channel();
    let mut welcomes = 0;
    let backend = FakeBackend::start(move |frame| {
        if frame["t"] == "hello" {
            welcomes += 1;
            if welcomes == 2 {
                entered.send(()).unwrap();
                ready.recv_timeout(DEADLINE).unwrap();
            }
            return vec![Reply::Frame(json!({"t":"welcome","protocol":1,
                "machine":"Friendly display name", "engine": if welcomes == 1 { "engine-a" } else { "engine-b" }}))];
        }
        if frame["t"] == "call" {
            return vec![Reply::Drop];
        }
        vec![]
    });
    let mut client = TestClient::start(backend.port);
    assert_eq!(backend.next()["t"], "hello");
    while !matches!(
        client.state(),
        super::connection_state::ConnectionState::Connected { .. }
    ) {}
    assert_eq!(
        client.engine_machine.lock().unwrap().as_deref(),
        Some("engine-a")
    );
    let _ = client.call("drop", None);
    assert_eq!(backend.next()["t"], "call");
    reconnecting.recv_timeout(DEADLINE).unwrap();
    assert_eq!(*client.engine_machine.lock().unwrap(), None);
    allow.send(()).unwrap();
    assert_eq!(backend.next()["t"], "hello");
    while !matches!(
        client.state(),
        super::connection_state::ConnectionState::Connected { .. }
    ) {}
    assert_eq!(
        client.engine_machine.lock().unwrap().as_deref(),
        Some("engine-b")
    );
}

#[test]
fn unapplied_scoped_pages_are_replayed_and_a_stale_delivery_cannot_ack_a_reconnect() {
    let backend = FakeBackend::start(|frame| {
        if frame["t"] == "hello" {
            return vec![Reply::Frame(
                json!({"t":"welcome","protocol":1,"machine":"friendly","engine":"owner"}),
            )];
        }
        if frame["t"] == "sub" {
            return vec![Reply::Frame(
                json!({"t":"engine_snapshot","id":frame["id"],"machine":"owner",
                "through":9,"items":[],"more":false}),
            )];
        }
        if frame["method"] == "drop" {
            return vec![Reply::Drop];
        }
        if frame["t"] == "call" {
            return vec![Reply::Frame(
                json!({"t":"result","id":frame["id"],"result":{}}),
            )];
        }
        vec![]
    });
    let client = TestClient::start(backend.port);
    let id = client.id();
    let (updates, mut received) = unbounded();
    client
        .events
        .send(Event::SubscribeEngineScope {
            id,
            topic: "sessions".into(),
            key: None,
            machine: Some("owner".into()),
            updates,
        })
        .unwrap();
    assert_eq!(backend.next()["t"], "hello");
    let first = backend.next();
    let EngineUpdate::Scoped(page) = block_on(received.recv()).unwrap() else {
        panic!("scoped page");
    };
    let (_, dropped) = client.call("drop", None);
    assert_eq!(
        backend.next()["t"],
        "call",
        "enqueue alone must not acknowledge the snapshot"
    );
    assert!(matches!(
        super::test_client::outcome(dropped),
        super::outcome::Outcome::Aborted
    ));
    assert_eq!(backend.next()["t"], "hello");
    let replay = backend.next();
    assert_eq!(replay["t"], "sub");
    assert_ne!(replay["id"], first["id"]);
    assert!(
        replay.get("after").is_none(),
        "the received but unapplied page cannot become the resume cursor"
    );
    client
        .events
        .send(Event::AcknowledgeEngine {
            handle: id,
            wire: page.wire,
            through: page.through,
            at: page.at,
        })
        .unwrap();
    let (_, barrier) = client.call("barrier", None);
    assert_eq!(
        backend.next()["t"],
        "call",
        "a prior connection's delivery cannot ack this one"
    );
    assert!(matches!(
        super::test_client::outcome(barrier),
        super::outcome::Outcome::Ok(_)
    ));
}

#[test]
fn bulk_connection_generation_changes_even_when_the_same_engine_reconnects() {
    let (allow, ready) = mpsc::channel();
    let (entered, reconnecting) = mpsc::channel();
    let mut welcomes = 0;
    let backend = FakeBackend::start(move |frame| {
        if frame["t"] == "hello" {
            welcomes += 1;
            if welcomes == 2 {
                entered.send(()).unwrap();
                ready.recv_timeout(DEADLINE).unwrap();
            }
            return vec![Reply::Frame(json!({"t":"welcome","protocol":1,
                "machine":"Friendly display name","engine":"same-engine"}))];
        }
        if frame["t"] == "call" {
            return vec![Reply::Drop];
        }
        vec![]
    });
    let mut client =
        TestClient::start_on(super::test_client::endpoint(backend.port), "/socket/bulk");
    assert_eq!(backend.next()["t"], "hello");
    while !matches!(
        client.state(),
        super::connection_state::ConnectionState::Connected { .. }
    ) {}
    let original = client
        .connection_generation
        .lock()
        .unwrap()
        .expect("connected generation");
    let (_, pending) = client.call("drop", None);
    assert_eq!(backend.next()["t"], "call");
    reconnecting.recv_timeout(DEADLINE).unwrap();
    assert_eq!(*client.connection_generation.lock().unwrap(), None);
    assert!(matches!(
        block_on(pending),
        Ok(super::outcome::Outcome::Aborted)
    ));
    allow.send(()).unwrap();
    assert_eq!(backend.next()["t"], "hello");
    while !matches!(
        client.state(),
        super::connection_state::ConnectionState::Connected { .. }
    ) {}
    let replacement = client
        .connection_generation
        .lock()
        .unwrap()
        .expect("replacement generation");
    assert_ne!(original, replacement);
    assert_eq!(
        client.engine_machine.lock().unwrap().as_deref(),
        Some("same-engine")
    );
}

#[test]
fn an_owned_scope_refresh_keeps_its_handle_and_cannot_revive_after_drop() {
    let backend = FakeBackend::start(|frame| {
        if let Some(welcome) = hello(frame) {
            return welcome;
        }
        match frame["t"].as_str() {
            Some("sub") => vec![Reply::Frame(
                json!({"t":"engine_snapshot","id":frame["id"],"machine":"owner","through":0,"items":[],"more":false}),
            )],
            Some("call") => vec![Reply::Frame(
                json!({"t":"result","id":frame["id"],"result":null}),
            )],
            _ => vec![],
        }
    });
    let client = TestClient::start(backend.port);
    let id = client.id();
    let (updates, received) = unbounded();
    client
        .events
        .send(Event::SubscribeEngineScope {
            id,
            topic: "providers".into(),
            key: None,
            machine: Some("owner".into()),
            updates,
        })
        .unwrap();
    let subscription =
        super::topic_subscription::TopicSubscription::new(id, client.events.clone(), received);
    let refresh = subscription.resnapshot_request();
    assert_eq!(backend.next()["t"], "hello");
    assert_eq!(backend.next()["id"], id);
    refresh();
    assert_eq!(backend.next(), json!({"t":"unsub","id":id}));
    let replacement = backend.next();
    assert_eq!(replacement["topic"], "providers");
    assert_ne!(replacement["id"], id);
    assert!(replacement.get("after").is_none());
    drop(subscription);
    refresh();
    let (_, barrier) = client.call("barrier", None);
    assert_eq!(backend.next(), json!({"t":"unsub","id":replacement["id"]}));
    assert_eq!(
        backend.next()["t"],
        "call",
        "a dropped handle must not recreate a scope"
    );
    assert!(matches!(
        super::test_client::outcome(barrier),
        super::outcome::Outcome::Ok(_)
    ));
}
