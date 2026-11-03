//! Wire behavior against a real local WebSocket, with exact producer frames.
use super::engine_update::EngineUpdate;
use super::event::Event;
use super::fake_backend::{FakeBackend, Reply, hello};
use super::test_client::DEADLINE;
use super::test_client::TestClient;
use futures_channel::mpsc::unbounded;
use futures_executor::block_on;
use serde_json::json;
use std::sync::mpsc;
use std::thread;

#[test]
fn engine_snapshot_ack_empty_cursor_replay_and_drop_use_the_existing_socket() {
    let backend = FakeBackend::start(|frame| {
        if let Some(welcome) = hello(frame) {
            return welcome;
        }
        match frame["t"].as_str() {
            Some("sub") => vec![Reply::Frame(
                json!({"t":"engine_snapshot","id":frame["id"],"machine":"owner","through":4,
                "items":[{"id":"prompt/s/c","state":"held"}],"more":false,
                "at":{"kind":"message.changed","entityId":"prompt/s/c","version":2}}),
            )],
            Some("ack") if frame["after"] == 4 => vec![Reply::Frame(
                json!({"t":"engine_events","id":frame["id"],"machine":"owner", "after":4,"through":8,"events":[],"more":false,
                "at":{"kind":"session.changed","entityId":"other","version":3}}),
            )],
            _ => vec![],
        }
    });
    let client = TestClient::start(backend.port);
    let id = client.id();
    let (updates, mut received) = unbounded();
    let (send, received_sync) = mpsc::channel();
    thread::spawn(move || {
        while let Ok(update) = block_on(received.recv()) {
            if send.send(update).is_err() {
                return;
            }
        }
    });
    client
        .events
        .send(Event::SubscribeEngine {
            id,
            message: "prompt/s/c".into(),
            machine: Some("owner".into()),
            updates,
        })
        .unwrap();
    assert_eq!(backend.next()["t"], "hello");
    assert_eq!(
        backend.next(),
        json!({"t":"sub","id":id,"stream":"engine","topic":"messages","key":"prompt/s/c","machine":"owner"})
    );
    assert!(
        matches!(received_sync.recv_timeout(DEADLINE).unwrap(), EngineUpdate::Snapshot(items) if items[0]["state"] == "held")
    );
    assert_eq!(
        backend.next(),
        json!({"t":"ack","id":id,"stream":"engine","after":4})
    );
    assert!(
        matches!(received_sync.recv_timeout(DEADLINE).unwrap(), EngineUpdate::Events(items) if items.is_empty())
    );
    assert_eq!(
        backend.next(),
        json!({"t":"ack","id":id,"stream":"engine","after":8})
    );
    client.events.send(Event::Unsubscribe { id }).unwrap();
    assert_eq!(backend.next(), json!({"t":"unsub","id":id}));
}

#[test]
fn engine_gap_resnapshots_under_a_fresh_wire_id_and_retains_cancellation_handle() {
    resnapshot_keeps_handle(false);
}

#[test]
fn rejected_old_engine_cursor_requests_one_fresh_snapshot() {
    resnapshot_keeps_handle(true);
}

fn resnapshot_keeps_handle(reject_cursor: bool) {
    let mut subscriptions = 0;
    let backend = FakeBackend::start(move |frame| {
        if let Some(welcome) = hello(frame) {
            return welcome;
        }
        if frame["t"] == "sub" {
            subscriptions += 1;
            let mut replies = vec![Reply::Frame(
                json!({"t":"engine_snapshot","id":frame["id"],"machine":"owner","through":2,"items":[],"more":false,
                "at":{"kind":"message.changed","entityId":"prompt/s/c","version":1}}),
            )];
            if subscriptions == 1 {
                replies.push(Reply::Frame(if reject_cursor {
                    json!({"t":"error","id":frame["id"],"code":"bad_args","message":"Invalid engine cursor"})
                } else {
                    json!({"t":"engine_events","id":frame["id"],"machine":"owner","after":3,"through":4,"events":[],"more":false})
                }));
            }
            return replies;
        }
        vec![]
    });
    let client = TestClient::start(backend.port);
    let id = client.id();
    let (updates, _received) = unbounded();
    client
        .events
        .send(Event::SubscribeEngine {
            id,
            message: "prompt/s/c".into(),
            machine: Some("owner".into()),
            updates,
        })
        .unwrap();
    assert_eq!(backend.next()["t"], "hello");
    assert_eq!(backend.next()["id"], id);
    assert_eq!(backend.next()["t"], "ack");
    assert_eq!(backend.next(), json!({"t":"unsub","id":id}));
    let resub = backend.next();
    assert_eq!(resub["t"], "sub");
    assert_ne!(resub["id"], id);
    assert!(resub.get("after").is_none());
    assert_eq!(backend.next()["t"], "ack");
    client.events.send(Event::Unsubscribe { id }).unwrap();
    assert_eq!(backend.next(), json!({"t":"unsub","id":resub["id"]}));
}

#[test]
fn engine_snapshot_finishes_before_ack_and_reconnect_replays_its_fingerprint() {
    let backend = FakeBackend::start(|frame| {
        if let Some(welcome) = hello(frame) {
            return welcome;
        }
        match frame["t"].as_str() {
            Some("sub") if frame.get("after").is_none() => vec![
                Reply::Frame(
                    json!({"t":"engine_snapshot","id":frame["id"],"machine":"owner","through":4,"items":[],"more":true}),
                ),
                Reply::Frame(
                    json!({"t":"engine_snapshot","id":frame["id"],"machine":"owner","through":4,"items":[],"more":false,
                    "at":{"kind":"message.changed","entityId":"prompt/s/c","version":2}}),
                ),
            ],
            Some("ack") if frame["after"] == 4 => vec![Reply::Drop],
            Some("sub") => vec![Reply::Frame(
                json!({"t":"engine_events","id":frame["id"],"machine":"owner","after":4,"through":5,
                "events":[{"machine":"owner","seq":5,"entityKind":"message","entityId":"prompt/s/c","version":3,"data":{"state":"failed"}}],
                "more":false,"at":{"kind":"message.changed","entityId":"prompt/s/c","version":3}}),
            )],
            _ => vec![],
        }
    });
    let client = TestClient::start(backend.port);
    let id = client.id();
    let (updates, _received) = unbounded();
    client
        .events
        .send(Event::SubscribeEngine {
            id,
            message: "prompt/s/c".into(),
            machine: Some("owner".into()),
            updates,
        })
        .unwrap();
    assert_eq!(backend.next()["t"], "hello");
    assert_eq!(backend.next()["t"], "sub");
    assert_eq!(
        backend.next(),
        json!({"t":"ack","id":id,"stream":"engine","after":4})
    );
    assert_eq!(backend.next()["t"], "hello");
    assert_eq!(
        backend.next(),
        json!({"t":"sub","id":id,"stream":"engine","topic":"messages","key":"prompt/s/c","machine":"owner","after":4,
        "at":{"kind":"message.changed","entityId":"prompt/s/c","version":2}})
    );
    assert_eq!(
        backend.next(),
        json!({"t":"ack","id":id,"stream":"engine","after":5})
    );
}

#[test]
fn local_engine_subscription_uses_welcome_engine_identity_and_receives_current_outcome() {
    welcome_engine_subscription(Some("durable-engine"), None, Some("durable-engine"));
}

#[test]
fn explicit_remote_owner_is_preserved_when_welcome_names_a_different_engine() {
    welcome_engine_subscription(
        Some("durable-engine"),
        Some("remote-owner"),
        Some("remote-owner"),
    );
}

#[test]
fn missing_welcome_engine_omits_machine_and_receives_the_server_local_snapshot() {
    welcome_engine_subscription(None, None, None);
}

fn welcome_engine_subscription(engine: Option<&str>, owner: Option<&str>, expected: Option<&str>) {
    let (engine, expected) = (engine.map(str::to_string), expected.map(str::to_string));
    let expected_wire = expected.clone();
    let backend = FakeBackend::start(move |frame| match frame["t"].as_str() {
        Some("hello") => {
            let mut welcome = json!({"t":"welcome", "protocol":1, "machine":"display-host"});
            if let Some(engine) = &engine {
                welcome["engine"] = json!(engine);
            }
            vec![Reply::Frame(welcome)]
        }
        Some("sub") => {
            if frame["machine"].as_str() != expected_wire.as_deref() {
                return vec![Reply::Frame(
                    json!({"t":"error","id":frame["id"],"code":"not_found","message":"Unknown mirror machine"}),
                )];
            }
            let machine = expected_wire.as_deref().unwrap_or("durable-engine");
            vec![
                Reply::Frame(
                    json!({"t":"engine_snapshot", "id":frame["id"], "machine":machine, "through":1,
                    "items":[{"id":"prompt/s/c", "entityKind":"message", "machine":machine,"toSessionId":"s","clientId":"c","state":"queued","reason":"auth_required","version":2}],
                    "more":false,"at":{"kind":"message.changed","entityId":"prompt/s/c","version":2}}),
                ),
                Reply::Frame(
                    json!({"t":"engine_events","id":frame["id"],"machine":machine,"after":1,"through":2,
                    "events":[{"machine":machine,"seq":2,"entityKind":"message","entityId":"prompt/s/c","version":3,"data":{"sessionId":"s","state":"held","reason":"needs_me"}}],
                    "more":false,"at":{"kind":"message.changed","entityId":"prompt/s/c","version":3}}),
                ),
            ]
        }
        _ => vec![],
    });
    let mut client = TestClient::start(backend.port);
    assert_eq!(
        client.state(),
        super::connection_state::ConnectionState::Connecting
    );
    assert_eq!(
        client.state(),
        super::connection_state::ConnectionState::Connected {
            machine: "display-host".into()
        }
    );
    let id = client.id();
    let (updates, mut received) = unbounded();
    let (send, received_sync) = mpsc::channel();
    thread::spawn(move || {
        while let Ok(update) = block_on(received.recv()) {
            if send.send(update).is_err() {
                return;
            }
        }
    });
    client
        .events
        .send(Event::SubscribeEngine {
            id,
            message: "prompt/s/c".into(),
            machine: owner.map(str::to_string),
            updates,
        })
        .unwrap();
    assert_eq!(backend.next()["t"], "hello");
    let subscribed = backend.next();
    assert_eq!(subscribed["t"], "sub");
    assert_eq!(subscribed["stream"], "engine");
    assert_eq!(subscribed["machine"].as_str(), expected.as_deref());
    assert!(subscribed.get("after").is_none());
    if expected.is_none() {
        assert!(subscribed.get("machine").is_none());
    }
    assert!(
        matches!(received_sync.recv_timeout(DEADLINE).unwrap(), EngineUpdate::Snapshot(items) if items[0]["state"] == "queued" && items[0]["reason"] == "auth_required")
    );
    assert!(
        matches!(received_sync.recv_timeout(DEADLINE).unwrap(), EngineUpdate::Events(items) if items[0]["data"]["state"] == "held")
    );
    assert_eq!(
        backend.next(),
        json!({"t":"ack","id":id,"stream":"engine","after":1})
    );
    assert_eq!(
        backend.next(),
        json!({"t":"ack","id":id,"stream":"engine","after":2})
    );
}

#[test]
fn scoped_snapshot_publishes_only_the_complete_cut_and_unsubscribes_on_drop() {
    let backend = FakeBackend::start(|frame| {
        if let Some(welcome) = hello(frame) { return welcome; }
        if frame["t"] == "sub" {
            return vec![
                Reply::Frame(json!({"t":"engine_snapshot","id":frame["id"],"machine":"owner","through":9,
                    "items":[{"entityKind":"session","id":"a","machine":"owner","version":1}],"more":true})),
                Reply::Frame(json!({"t":"engine_snapshot","id":frame["id"],"machine":"owner","through":9,
                    "items":[{"entityKind":"session","id":"b","machine":"owner","version":2}],"more":false,
                    "at":{"kind":"session.changed","entityId":"b","version":2}})),
            ];
        }
        vec![]
    });
    let client = TestClient::start(backend.port);
    let id = client.id();
    let (updates, mut received) = unbounded();
    client.events.send(Event::SubscribeEngineScope {
        id, topic: "sessions".into(), key: None, machine: Some("owner".into()), updates,
    }).unwrap();
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
            client.events.send(Event::AcknowledgeEngine {
                handle:id, wire:page.wire, through:page.through, at:page.at,
            }).unwrap();
        }
        update => panic!("expected a complete scoped snapshot, got {update:?}"),
    }
    assert_eq!(backend.next(), json!({"t":"ack","id":id,"stream":"engine","after":9}));
    client.events.send(Event::Unsubscribe { id }).unwrap();
    assert_eq!(backend.next(), json!({"t":"unsub","id":id}));
}

#[test]
fn engine_identity_is_cleared_until_the_reconnected_welcome_names_its_owner() {
    let (allow, ready) = mpsc::channel();
    let mut welcomes = 0;
    let backend = FakeBackend::start(move |frame| {
        if frame["t"] == "hello" {
            welcomes += 1;
            if welcomes == 2 { ready.recv_timeout(DEADLINE).unwrap(); }
            return vec![Reply::Frame(json!({"t":"welcome","protocol":1,
                "machine":"Friendly display name", "engine": if welcomes == 1 { "engine-a" } else { "engine-b" }}))];
        }
        if frame["t"] == "call" { return vec![Reply::Drop]; }
        vec![]
    });
    let mut client = TestClient::start(backend.port);
    assert_eq!(backend.next()["t"], "hello");
    while !matches!(client.state(), super::connection_state::ConnectionState::Connected { .. }) {}
    assert_eq!(client.engine_machine.lock().unwrap().as_deref(), Some("engine-a"));
    let _ = client.call("drop", None);
    assert_eq!(backend.next()["t"], "call");
    assert_eq!(backend.next()["t"], "hello");
    assert_eq!(*client.engine_machine.lock().unwrap(), None);
    allow.send(()).unwrap();
    while !matches!(client.state(), super::connection_state::ConnectionState::Connected { .. }) {}
    assert_eq!(client.engine_machine.lock().unwrap().as_deref(), Some("engine-b"));
}

#[test]
fn unapplied_scoped_pages_are_replayed_and_a_stale_delivery_cannot_ack_a_reconnect() {
    let backend = FakeBackend::start(|frame| {
        if frame["t"] == "hello" {
            return vec![Reply::Frame(json!({"t":"welcome","protocol":1,"machine":"friendly","engine":"owner"}))];
        }
        if frame["t"] == "sub" {
            return vec![Reply::Frame(json!({"t":"engine_snapshot","id":frame["id"],"machine":"owner",
                "through":9,"items":[],"more":false}))];
        }
        if frame["method"] == "drop" { return vec![Reply::Drop]; }
        if frame["t"] == "call" {
            return vec![Reply::Frame(json!({"t":"result","id":frame["id"],"result":{}}))];
        }
        vec![]
    });
    let client = TestClient::start(backend.port);
    let id = client.id();
    let (updates, mut received) = unbounded();
    client.events.send(Event::SubscribeEngineScope {
        id, topic:"sessions".into(), key:None, machine:Some("owner".into()), updates,
    }).unwrap();
    assert_eq!(backend.next()["t"], "hello");
    let first = backend.next();
    let EngineUpdate::Scoped(page) = block_on(received.recv()).unwrap() else { panic!("scoped page"); };
    let (_, dropped) = client.call("drop", None);
    assert_eq!(backend.next()["t"], "call", "enqueue alone must not acknowledge the snapshot");
    assert!(matches!(super::test_client::outcome(dropped), super::outcome::Outcome::Aborted));
    assert_eq!(backend.next()["t"], "hello");
    let replay = backend.next();
    assert_eq!(replay["t"], "sub");
    assert_ne!(replay["id"], first["id"]);
    assert!(replay.get("after").is_none(), "the received but unapplied page cannot become the resume cursor");
    client.events.send(Event::AcknowledgeEngine {
        handle:id, wire:page.wire, through:page.through, at:page.at,
    }).unwrap();
    let (_, barrier) = client.call("barrier", None);
    assert_eq!(backend.next()["t"], "call", "a prior connection's delivery cannot ack this one");
    assert!(matches!(super::test_client::outcome(barrier), super::outcome::Outcome::Ok(_)));
}
