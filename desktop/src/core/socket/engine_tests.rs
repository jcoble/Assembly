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
