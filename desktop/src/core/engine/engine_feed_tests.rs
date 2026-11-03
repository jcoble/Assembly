//! Source-derived socket fixtures; no provider or native acceptance is claimed.
use super::{EngineFeed, EngineScope};
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply};
use crate::core::socket::test_wait::until;
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use serde_json::json;

#[gpui::test]
async fn two_consumers_share_a_scope_and_the_last_drop_unsubscribes(cx: &mut TestAppContext) {
    let backend = FakeBackend::start(|frame| match frame["t"].as_str() {
        Some("hello") => vec![Reply::Frame(
            json!({"t":"welcome","protocol":1,"machine":"friendly","engine":"owner"}),
        )],
        Some("sub") => vec![Reply::Frame(
            json!({"t":"engine_snapshot","id":frame["id"],"machine":"owner",
                "through":1,"items":[],"more":false}),
        )],
        Some("call") => vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":{}}),
        )],
        _ => vec![],
    });
    cx.executor().allow_parking();
    let socket = cx.update(|cx| {
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        )
    });
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _heard = cx.update(|cx| {
        cx.observe(&socket, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    until("connected", &mut changes, &changed, || {
        cx.update(|cx| matches!(socket.read(cx).state(), ConnectionState::Connected { .. }))
    })
    .await;
    assert_eq!(backend.next()["t"], "hello");
    let scope = EngineScope {
        machine: "owner".into(),
        topic: "sessions".into(),
        key: None,
    };
    let feed = cx.update(EngineFeed::global);
    let (first, second) = cx.update(|cx| {
        feed.update(cx, |feed, cx| {
            (
                feed.watch(scope.clone(), cx).unwrap(),
                feed.watch(scope.clone(), cx).unwrap(),
            )
        })
    });
    let tell = changed.clone();
    let _feed_heard = cx.update(|cx| {
        cx.observe(&feed, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    until("complete scope", &mut changes, &changed, || {
        cx.update(|cx| feed.read(cx).loaded(&scope))
    })
    .await;
    let sub = backend.next();
    assert_eq!(sub["t"], "sub");
    assert_eq!(backend.next()["t"], "ack");
    first.request_snapshot();
    first.request_snapshot(); // Redundant refresh while the fresh snapshot is pending is coalesced.
    assert!(!cx.update(|cx| feed.read(cx).loaded(&scope)));
    until("replacement complete scope", &mut changes, &changed, || {
        cx.update(|cx| feed.read(cx).loaded(&scope))
    }).await;
    assert_eq!(backend.next(), json!({"t":"unsub","id":sub["id"]}));
    let replacement = backend.next();
    assert_eq!(replacement["t"], "sub");
    assert_ne!(replacement["id"], sub["id"]);
    assert!(replacement.get("after").is_none());
    assert_eq!(backend.next()["t"], "ack");
    drop(first);
    let call = cx.update(|cx| {
        socket
            .read(cx)
            .call::<serde_json::Value>("test", "barrier", json!({}), None)
    });
    cx.run_until_parked();
    assert_eq!(
        backend.next()["t"],
        "call",
        "the shared scope is still subscribed"
    );
    let _ = call.await;
    drop(second);
    cx.run_until_parked();
    assert_eq!(backend.next(), json!({"t":"unsub","id":replacement["id"]}));
}

#[gpui::test]
async fn sparse_events_fetch_bounded_display_pages_before_the_stream_ack(cx: &mut TestAppContext) {
    let backend = FakeBackend::start(|frame| match frame["t"].as_str() {
        Some("hello") => vec![Reply::Frame(
            json!({"t":"welcome","protocol":1,"machine":"friendly","engine":"owner"}),
        )],
        Some("sub") => vec![Reply::Frame(
            json!({"t":"engine_snapshot","id":frame["id"],"machine":"owner",
            "through":1,"items":[],"more":false}),
        )],
        Some("ack") if frame["after"] == 1 => {
            vec![Reply::Frame(json!({"t":"engine_events","id":frame["id"],
            "machine":"owner","after":1,"through":3,"events":[
                {"machine":"owner","seq":2,"entityKind":"session","entityId":"missing","version":1},
                {"machine":"owner","seq":3,"entityKind":"session","entityId":"present","version":2}
            ]}))]
        }
        Some("call") if frame["method"] == "entities" => {
            let result = if frame["args"]["after"].is_null() {
                json!({"items":[{"machine":"owner","entityKind":"session","id":"present","version":2,
                    "title":"Actual display","state":"working"}],"next":"opaque-input-cursor"})
            } else {
                json!({"items":[]})
            };
            vec![Reply::Frame(
                json!({"t":"result","id":frame["id"],"result":result}),
            )]
        }
        _ => vec![],
    });
    cx.executor().allow_parking();
    let (socket, bulk) = cx.update(|cx| {
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        (
            SocketClient::start(endpoint.clone(), cx),
            SocketClient::start_bulk(endpoint, cx),
        )
    });
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _heard = cx.update(|cx| {
        cx.observe(&socket, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    let tell = changed.clone();
    let _bulk_heard = cx.update(|cx| {
        cx.observe(&bulk, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    until("both sockets connected", &mut changes, &changed, || {
        cx.update(|cx| {
            matches!(socket.read(cx).state(), ConnectionState::Connected { .. })
                && matches!(bulk.read(cx).state(), ConnectionState::Connected { .. })
        })
    })
    .await;
    assert_eq!(backend.next()["t"], "hello");
    assert_eq!(backend.next()["t"], "hello");
    let scope = EngineScope {
        machine: "owner".into(),
        topic: "sessions".into(),
        key: None,
    };
    let feed = cx.update(EngineFeed::global);
    let tell = changed.clone();
    let _feed_heard = cx.update(|cx| {
        cx.observe(&feed, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    let _watch = cx.update(|cx| feed.update(cx, |feed, cx| feed.watch(scope.clone(), cx).unwrap()));
    let key = super::EngineKey {
        machine: "owner".into(),
        kind: "session".into(),
        id: "present".into(),
    };
    until("full display applied", &mut changes, &changed, || {
        cx.update(|cx| feed.read(cx).item(&key).is_some())
    })
    .await;
    assert_eq!(backend.next()["t"], "sub");
    assert_eq!(backend.next()["after"], 1);
    let first = backend.next();
    let second = backend.next();
    assert_eq!(first["method"], "entities");
    assert_eq!(first["args"]["machine"], "owner");
    assert_eq!(first["args"]["refs"].as_array().unwrap().len(), 2);
    assert_eq!(second["args"]["refs"], first["args"]["refs"]);
    assert_eq!(second["args"]["after"], "opaque-input-cursor");
    assert_eq!(
        backend.next()["after"],
        3,
        "stream ACK follows the final display page"
    );
    cx.update(|cx| {
        let feed = feed.read(cx);
        assert_eq!(feed.items(&scope).len(), 1);
        assert_eq!(feed.item(&key).unwrap().value["title"], "Actual display");
    });
}

#[gpui::test]
async fn a_replaced_bulk_connection_leaves_the_cut_unapplied_until_pushed_reconnect(
    cx: &mut TestAppContext,
) {
    let (allow, ready) = std::sync::mpsc::channel();
    let mut welcomes = 0;
    let mut reads = 0;
    let backend = FakeBackend::start(move |frame| match frame["t"].as_str() {
        Some("hello") => {
            welcomes += 1;
            if welcomes == 3 {
                ready
                    .recv_timeout(crate::core::socket::test_client::DEADLINE)
                    .unwrap();
            }
            vec![Reply::Frame(
                json!({"t":"welcome","protocol":1,"machine":"friendly","engine":"owner"}),
            )]
        }
        Some("sub") => vec![Reply::Frame(
            json!({"t":"engine_snapshot","id":frame["id"],"machine":"owner",
            "through":1,"items":[],"more":false}),
        )],
        Some("ack") if frame["after"] == 1 => {
            vec![Reply::Frame(json!({"t":"engine_events","id":frame["id"],
            "machine":"owner","after":1,"through":2,"events":[
                {"machine":"owner","seq":2,"entityKind":"session","entityId":"s","version":2}
            ]}))]
        }
        Some("call") if frame["method"] == "entities" => {
            reads += 1;
            if reads == 1 {
                return vec![Reply::Drop];
            }
            vec![Reply::Frame(
                json!({"t":"result","id":frame["id"],"result":{"items":[
                    {"machine":"owner","entityKind":"session","id":"s","version":2,"title":"After reconnect"}
                ]}}),
            )]
        }
        _ => vec![],
    });
    cx.executor().allow_parking();
    let (socket, bulk) = cx.update(|cx| {
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        (
            SocketClient::start(endpoint.clone(), cx),
            SocketClient::start_bulk(endpoint, cx),
        )
    });
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _heard = cx.update(|cx| {
        cx.observe(&socket, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    let tell = changed.clone();
    let _bulk_heard = cx.update(|cx| {
        cx.observe(&bulk, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    until("both connections ready", &mut changes, &changed, || {
        cx.update(|cx| {
            matches!(socket.read(cx).state(), ConnectionState::Connected { .. })
                && matches!(bulk.read(cx).state(), ConnectionState::Connected { .. })
        })
    })
    .await;
    assert_eq!(backend.next()["t"], "hello");
    assert_eq!(backend.next()["t"], "hello");
    let feed = cx.update(EngineFeed::global);
    let tell = changed.clone();
    let _feed_heard = cx.update(|cx| {
        cx.observe(&feed, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    let scope = EngineScope {
        machine: "owner".into(),
        topic: "sessions".into(),
        key: None,
    };
    let _watch = cx.update(|cx| feed.update(cx, |feed, cx| feed.watch(scope.clone(), cx).unwrap()));
    until(
        "read cancelled by disconnect",
        &mut changes,
        &changed,
        || cx.update(|cx| feed.read(cx).error().is_some()),
    )
    .await;
    assert!(cx.update(|cx| feed.read(cx).items(&scope).is_empty()));
    assert_eq!(backend.next()["t"], "sub");
    assert_eq!(backend.next()["after"], 1);
    assert_eq!(backend.next()["method"], "entities");
    // Release the held welcome directly: FakeBackend publishes hello only after its handler returns.
    allow.send(()).unwrap();
    assert_eq!(backend.next()["t"], "hello");
    let key = super::EngineKey {
        machine: "owner".into(),
        kind: "session".into(),
        id: "s".into(),
    };
    until(
        "display read after pushed reconnect",
        &mut changes,
        &changed,
        || cx.update(|cx| feed.read(cx).item(&key).is_some()),
    )
    .await;
    assert_eq!(backend.next()["method"], "entities");
    assert_eq!(backend.next()["after"], 2);
    assert!(cx.update(|cx| feed.read(cx).error().is_none()));
}

#[gpui::test]
async fn refresh_cancels_a_held_old_entities_read_and_applies_the_new_cut_without_its_reply(cx: &mut TestAppContext) {
    use std::sync::{Arc, Mutex};
    #[derive(Default)]
    struct Held { id: Option<serde_json::Value>, cancelled: bool, subscriptions: usize }
    let held = Arc::new(Mutex::new(Held::default()));
    let seen = held.clone();
    let (changed, mut changes) = unbounded();
    let notify = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        let mut held = seen.lock().unwrap();
        let replies = match frame["t"].as_str() {
            Some("hello") => vec![Reply::Frame(json!({"t":"welcome","protocol":1,"machine":"friendly","engine":"owner"}))],
            Some("sub") => {
                held.subscriptions += 1;
                let fresh = held.subscriptions > 1;
                let items = if fresh { vec![json!({"entityKind":"session","id":"s","machine":"owner","version":3,"title":"Fresh cut"})] } else { vec![] };
                vec![Reply::Frame(json!({"t":"engine_snapshot","id":frame["id"],"machine":"owner","through":if fresh {3} else {1},"items":items,"more":false}))]
            }
            Some("ack") if frame["after"] == 1 => vec![Reply::Frame(json!({"t":"engine_events","id":frame["id"],"machine":"owner","after":1,"through":2,
                "events":[{"machine":"owner","seq":2,"entityKind":"session","entityId":"s","version":2}]}))],
            Some("call") if frame["method"] == "entities" => { held.id = Some(frame["id"].clone()); vec![] }
            Some("cancel") => { if held.id.as_ref() == Some(&frame["id"]) { held.cancelled = true; } vec![] }
            _ => vec![],
        };
        let _ = notify.unbounded_send(());
        replies
    });
    cx.executor().allow_parking();
    let endpoint = Endpoint { port: backend.port, token: "secret".into() };
    let (main, bulk) = cx.update(|cx| (SocketClient::start(endpoint.clone(),cx),SocketClient::start_bulk(endpoint,cx)));
    let a = changed.clone(); let b = changed.clone();
    let (_main, _bulk) = cx.update(|cx| (
        cx.observe(&main,move |_,_| { let _=a.unbounded_send(()); }),
        cx.observe(&bulk,move |_,_| { let _=b.unbounded_send(()); })
    ));
    until("both sockets", &mut changes, &changed, || cx.update(|cx|
        main.read(cx).connection_generation().is_some() && bulk.read(cx).connection_generation().is_some())).await;
    let feed = cx.update(EngineFeed::global);
    let scope = EngineScope { machine:"owner".into(), topic:"sessions".into(), key:None };
    let watch = cx.update(|cx| feed.update(cx,|feed,cx|feed.watch(scope.clone(),cx).unwrap()));
    let signal=changed.clone();
    let _feed=cx.update(|cx|cx.observe(&feed,move|_,_|{let _=signal.unbounded_send(());}));
    until("old entities call held",&mut changes,&changed,||held.lock().unwrap().id.is_some()).await;
    watch.request_snapshot();
    assert!(!cx.update(|cx|feed.read(cx).loaded(&scope)));
    until("fresh cut and old read cancelled",&mut changes,&changed,|| {
        held.lock().unwrap().cancelled && cx.update(|cx|feed.read(cx).loaded(&scope)
            && feed.read(cx).item(&super::EngineKey{machine:"owner".into(),kind:"session".into(),id:"s".into()})
                .is_some_and(|item|item.version==3 && item.value["title"]=="Fresh cut"))
    }).await;
    assert_eq!(held.lock().unwrap().subscriptions,2);
    assert_eq!(cx.update(|cx|feed.read(cx).store.cursor(&scope).unwrap().through),3);
    // The held entities call has deliberately never received a result frame.
    drop(watch);
}
