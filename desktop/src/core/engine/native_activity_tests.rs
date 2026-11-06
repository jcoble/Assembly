//! Native cache fixtures prove persisted display application, never provider history or execution authority.
use super::native_activity::{NativeState, SubagentItem, decode_page};
use serde_json::json;
fn item(id: &str, seq: i64, state: &str) -> SubagentItem {
    serde_json::from_value(json!({"itemId":id,"seq":seq,"sessionId":"recorded-parent","title":"native","description":null,"state":state,"stopReason":null,"error":null,"toolCallId":null})).unwrap()
}
#[test]
fn a_late_native_page_cannot_replace_a_newer_persisted_display_version() {
    let mut state = NativeState::default();
    state.install(vec![item("subagent:a", 8, "idle")]).unwrap();
    state
        .install(vec![
            item("subagent:a", 7, "running"),
            item("subagent:b", 9, "running"),
        ])
        .unwrap();
    assert_eq!(state.items[0].seq, 8);
    assert_eq!(state.items[0].state, "idle");
    assert_eq!(state.items.len(), 2);
}
#[test]
fn a_complete_native_refresh_removes_only_missing_cards_and_preserves_the_wire_order() {
    let mut state = NativeState::default();
    state
        .install(vec![item("a", 1, "running"), item("b", 2, "idle")])
        .unwrap();
    state.install(vec![item("b", 3, "idle")]).unwrap();
    assert_eq!(state.items.len(), 1);
    assert_eq!(state.items[0].item_id, "b");
}
#[test]
fn native_pages_keep_parent_payload_identity_and_exact_immutable_item_cursor() {
    let page =
        decode_page(json!({"items":[item("subagent:a",9,"running")],"next":"subagent:a"})).unwrap();
    assert_eq!(page.items[0].session_id, "recorded-parent");
    assert_eq!(page.next.as_deref(), Some("subagent:a"));
    assert!(decode_page(json!({"items":vec![item("a",1,"running");201],"next":null})).is_err());
}
#[test]
fn oversized_or_duplicate_native_display_items_refuse_instead_of_silently_truncating() {
    let mut large = item("a", 1, "running");
    large.title = "x".repeat(32 * 1024);
    assert!(decode_page(json!({"items":[large],"next":null})).is_err());
    let mut state = NativeState::default();
    state.install(vec![item("a", 4, "idle")]).unwrap();
    assert!(
        state
            .install(vec![item("a", 5, "running"), item("a", 6, "idle")])
            .is_err()
    );
    assert_eq!(state.items[0].seq, 4);
}

use super::{EngineFeed, SessionKey};
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply},
    row_source::ExpectedRowSource,
    test_wait::until,
};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use std::sync::{Arc, Mutex};
#[derive(Default)]
struct Seen {
    subs: Vec<(String, serde_json::Value)>,
    pages: Vec<serde_json::Value>,
    unsubs: Vec<serde_json::Value>,
    cancels: Vec<serde_json::Value>,
}
fn source(machine: &str, generation: u64) -> ExpectedRowSource {
    ExpectedRowSource {
        machine: machine.into(),
        generation,
        route_machine: Some(format!("route-{machine}")),
    }
}
fn parent(machine: &str) -> SessionKey {
    SessionKey {
        machine: machine.into(),
        id: "same-parent".into(),
    }
}
fn backend(
    seen: Arc<Mutex<Seen>>,
    changed: futures_channel::mpsc::UnboundedSender<()>,
    hold: bool,
) -> FakeBackend {
    FakeBackend::start(move |frame| {
        let id = frame["id"].clone();
        let mut seen = seen.lock().unwrap();
        let replies = match (
            frame["t"].as_str(),
            frame["feature"].as_str(),
            frame["method"].as_str(),
            frame["topic"].as_str(),
        ) {
            (Some("hello"), ..) => vec![Reply::Frame(
                json!({"t":"welcome","protocol":1,"machine":"box","engine":"desktop"}),
            )],
            (Some("sub"), _, _, Some("rows")) => {
                seen.subs.push((
                    frame["expectedSource"]["machine"].as_str().unwrap().into(),
                    id.clone(),
                ));
                vec![Reply::Frame(
                    json!({"t":"state","sub":id,"seq":0,"items":[]}),
                )]
            }
            (Some("unsub"), ..) => {
                seen.unsubs.push(id);
                vec![]
            }
            (Some("cancel"), ..) => {
                seen.cancels.push(id);
                vec![]
            }
            (Some("call"), Some("rows"), Some("subagents"), _) => {
                seen.pages.push(frame["args"].clone());
                if hold {
                    vec![]
                } else {
                    let args = &frame["args"];
                    let machine = args["expectedSource"]["machine"].as_str().unwrap();
                    let mut shown = item(
                        if args["after"].is_null() {
                            "subagent:a"
                        } else {
                            "subagent:b"
                        },
                        args["expectedSource"]["generation"].as_i64().unwrap(),
                        if machine == "a" { "running" } else { "idle" },
                    );
                    shown.title = machine.into();
                    vec![Reply::Frame(
                        json!({"t":"result","id":id,"result":{"items":[shown],"next":if args["after"].is_null(){Some("subagent:a")}else{None}}}),
                    )]
                }
            }
            (Some("call"), Some("fixture"), Some("retire"), _) => {
                let machine = frame["args"]["machine"].as_str().unwrap();
                let sub = seen
                    .subs
                    .iter()
                    .find(|(owner, _)| owner == machine)
                    .unwrap()
                    .1
                    .clone();
                vec![
                    Reply::Frame(
                        json!({"t":"error","id":sub,"code":"source_changed","message":"Actual fixture source retired."}),
                    ),
                    Reply::Frame(json!({"t":"result","id":id,"result":null})),
                ]
            }
            _ => vec![],
        };
        drop(seen);
        let _ = changed.unbounded_send(());
        replies
    })
}
fn start(
    port: u16,
    cx: &mut gpui_kit::App,
) -> (
    gpui_kit::Entity<SocketClient>,
    gpui_kit::Entity<SocketClient>,
    gpui_kit::Entity<EngineFeed>,
) {
    gpui_kit::init(cx);
    let endpoint = Endpoint {
        port,
        token: "secret".into(),
    };
    let main = SocketClient::start(endpoint.clone(), cx);
    let bulk = SocketClient::start_bulk(endpoint, cx);
    (main, bulk, EngineFeed::global(cx))
}
#[gpui::test]
async fn two_native_consumers_share_one_source_watch_and_all_real_item_pages(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Seen::default()));
    let (changed, mut changes) = unbounded();
    let backend = backend(seen.clone(), changed.clone(), false);
    cx.executor().allow_parking();
    let (main, bulk, feed) = cx.update(|cx| start(backend.port, cx));
    let ready = changed.clone();
    let ready_bulk = changed.clone();
    let fed = changed.clone();
    let _main = cx.update(|cx| {
        cx.observe(&main, move |_, _| {
            let _ = ready.unbounded_send(());
        })
    });
    let _bulk = cx.update(|cx| {
        cx.observe(&bulk, move |_, _| {
            let _ = ready_bulk.unbounded_send(());
        })
    });
    let _feed = cx.update(|cx| {
        cx.observe(&feed, move |_, _| {
            let _ = fed.unbounded_send(());
        })
    });
    until("both native sockets", &mut changes, &changed, || {
        cx.update(|cx| {
            main.read(cx).connection_generation().is_some()
                && bulk.read(cx).connection_generation().is_some()
        })
    })
    .await;
    let (first, second) = cx.update(|cx| {
        feed.update(cx, |feed, cx| {
            (
                feed.watch_native(parent("a"), source("a", 7), cx).unwrap(),
                feed.watch_native(parent("a"), source("a", 7), cx).unwrap(),
            )
        })
    });
    until(
        "shared native pages installed",
        &mut changes,
        &changed,
        || first.loaded() && second.loaded(),
    )
    .await;
    cx.update(|cx| {
        assert_eq!(
            feed.read(cx)
                .subagents_of(&parent("a"))
                .iter()
                .map(|item| item.item_id.as_str())
                .collect::<Vec<_>>(),
            ["subagent:a", "subagent:b"]
        )
    });
    assert_eq!(seen.lock().unwrap().subs.len(), 1);
    assert_eq!(seen.lock().unwrap().pages.len(), 2);
    for page in &seen.lock().unwrap().pages {
        assert_eq!(
            page["expectedSource"],
            serde_json::to_value(source("a", 7)).unwrap()
        );
    }
    drop(first);
    cx.run_until_parked();
    assert!(seen.lock().unwrap().unsubs.is_empty());
    drop(second);
    until(
        "final native watch unsubscribed",
        &mut changes,
        &changed,
        || seen.lock().unwrap().unsubs.len() == 1,
    )
    .await;
    cx.update(|cx| assert!(feed.read(cx).subagents_of(&parent("a")).is_empty()));
}
#[gpui::test]
async fn identical_native_ids_are_source_qualified_and_old_lease_drop_cannot_clear_replacement(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Seen::default()));
    let (changed, mut changes) = unbounded();
    let backend = backend(seen.clone(), changed.clone(), false);
    cx.executor().allow_parking();
    let (main, bulk, feed) = cx.update(|cx| start(backend.port, cx));
    let ready = changed.clone();
    let ready_bulk = changed.clone();
    let fed = changed.clone();
    let _main = cx.update(|cx| {
        cx.observe(&main, move |_, _| {
            let _ = ready.unbounded_send(());
        })
    });
    let _bulk = cx.update(|cx| {
        cx.observe(&bulk, move |_, _| {
            let _ = ready_bulk.unbounded_send(());
        })
    });
    let _feed = cx.update(|cx| {
        cx.observe(&feed, move |_, _| {
            let _ = fed.unbounded_send(());
        })
    });
    until("native sockets", &mut changes, &changed, || {
        cx.update(|cx| {
            main.read(cx).connection_generation().is_some()
                && bulk.read(cx).connection_generation().is_some()
        })
    })
    .await;
    let (a, b) = cx.update(|cx| {
        feed.update(cx, |feed, cx| {
            (
                feed.watch_native(parent("a"), source("a", 7), cx).unwrap(),
                feed.watch_native(parent("b"), source("b", 8), cx).unwrap(),
            )
        })
    });
    until(
        "both source-qualified native parents",
        &mut changes,
        &changed,
        || a.loaded() && b.loaded(),
    )
    .await;
    cx.update(|cx| {
        let feed = feed.read(cx);
        assert_eq!(feed.subagents_of(&parent("a"))[0].title, "a");
        assert_eq!(feed.subagents_of(&parent("b"))[0].title, "b");
    });
    assert!(
        cx.update(|cx| feed.update(cx, |feed, cx| feed.watch_native(
            parent("a"),
            source("a", 9),
            cx
        )))
        .is_err()
    );
    let retire = cx.update(|cx| {
        main.read(cx)
            .call::<serde_json::Value>("fixture", "retire", json!({"machine":"a"}), None)
    });
    let _ = retire.await;
    until(
        "only the retired source is invalidated",
        &mut changes,
        &changed,
        || a.error().is_some(),
    )
    .await;
    cx.update(|cx| {
        assert!(feed.read(cx).subagents_of(&parent("a")).is_empty());
        assert_eq!(feed.read(cx).subagents_of(&parent("b")).len(), 2);
    });
    let replacement = cx.update(|cx| {
        feed.update(cx, |feed, cx| {
            feed.watch_native(parent("a"), source("a", 9), cx).unwrap()
        })
    });
    until("replacement native source", &mut changes, &changed, || {
        replacement.loaded()
    })
    .await;
    drop(a);
    cx.run_until_parked();
    cx.update(|cx| assert_eq!(feed.read(cx).subagents_of(&parent("a"))[0].seq, 9));
}
#[gpui::test]
async fn dropping_the_last_native_watch_cancels_a_held_bulk_read_and_parent_subscription(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Seen::default()));
    let (changed, mut changes) = unbounded();
    let backend = backend(seen.clone(), changed.clone(), true);
    cx.executor().allow_parking();
    let (main, bulk, feed) = cx.update(|cx| start(backend.port, cx));
    let ready = changed.clone();
    let ready_bulk = changed.clone();
    let _main = cx.update(|cx| {
        cx.observe(&main, move |_, _| {
            let _ = ready.unbounded_send(());
        })
    });
    let _bulk = cx.update(|cx| {
        cx.observe(&bulk, move |_, _| {
            let _ = ready_bulk.unbounded_send(());
        })
    });
    until("native sockets", &mut changes, &changed, || {
        cx.update(|cx| {
            main.read(cx).connection_generation().is_some()
                && bulk.read(cx).connection_generation().is_some()
        })
    })
    .await;
    let watch = cx.update(|cx| {
        feed.update(cx, |feed, cx| {
            feed.watch_native(parent("a"), source("a", 7), cx).unwrap()
        })
    });
    until(
        "held native projection read",
        &mut changes,
        &changed,
        || seen.lock().unwrap().pages.len() == 1,
    )
    .await;
    drop(watch);
    until(
        "owned native read and parent watch cancelled",
        &mut changes,
        &changed,
        || {
            let seen = seen.lock().unwrap();
            seen.cancels.len() == 1 && seen.unsubs.len() == 1
        },
    )
    .await;
    cx.update(|cx| assert!(feed.read(cx).subagents_of(&parent("a")).is_empty()));
}

#[gpui::test]
async fn native_pushes_coalesce_behind_one_opaque_complete_pass_and_never_install_a_partial_page(
    cx: &mut TestAppContext,
) {
    use crate::core::socket::outcome::Outcome;
    use serde_json::Value;
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let pages = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = pages.clone();
    let mut sub = None;
    let mut held_first = None;
    let mut held_second = None;
    let backend = FakeBackend::start(move |frame| {
        let id = frame["id"].clone();
        let frames = match (
            frame["t"].as_str(),
            frame["feature"].as_str(),
            frame["method"].as_str(),
        ) {
            (Some("hello"), ..) => vec![Reply::Frame(
                json!({"t":"welcome","protocol":1,"machine":"box","engine":"desktop"}),
            )],
            (Some("sub"), ..) => {
                sub = Some(id.clone());
                vec![Reply::Frame(
                    json!({"t":"state","sub":id,"seq":10,"items":[]}),
                )]
            }
            (Some("call"), Some("rows"), Some("subagents")) => {
                let mut pages = observed.lock().unwrap();
                pages.push(frame["args"].clone());
                match pages.len() {
                    1 => {
                        held_first = Some(id);
                        vec![]
                    }
                    2 => {
                        assert_eq!(frame["args"]["after"], "opaque:fixed-cut:first");
                        held_second = Some(id);
                        vec![]
                    }
                    3 => {
                        assert!(frame["args"]["after"].is_null());
                        vec![Reply::Frame(
                            json!({"t":"result","id":id,"result":{"items":[item("subagent:a",13,"idle"),item("subagent:b",10,"running")],"next":null}}),
                        )]
                    }
                    _ => panic!("redundant native refresh was queued instead of coalesced"),
                }
            }
            (Some("call"), Some("fixture"), Some("push")) => {
                let mut frames:Vec<_>=(11..=13).map(|seq|Reply::Frame(json!({"t":"rows","sub":sub,"row":{"sessionId":"same-parent","seq":seq,"prevSeq":seq-1,"firstSeq":null,"itemId":"subagent:a","runId":null,"kind":"subagent","createdAt":seq,"payload":{}}}))).collect();
                frames.push(Reply::Frame(json!({"t":"result","id":id,"result":null})));
                frames
            }
            (Some("call"), Some("fixture"), Some("release_first")) => vec![
                Reply::Frame(
                    json!({"t":"result","id":held_first.take().unwrap(),"result":{"items":[item("subagent:a",9,"running")],"next":"opaque:fixed-cut:first"}}),
                ),
                Reply::Frame(json!({"t":"result","id":id,"result":null})),
            ],
            (Some("call"), Some("fixture"), Some("release_second")) => vec![
                Reply::Frame(
                    json!({"t":"result","id":held_second.take().unwrap(),"result":{"items":[item("subagent:b",10,"running")],"next":null}}),
                ),
                Reply::Frame(json!({"t":"result","id":id,"result":null})),
            ],
            _ => vec![],
        };
        let _ = tell.unbounded_send(());
        frames
    });
    cx.executor().allow_parking();
    let (main, bulk, feed) = cx.update(|cx| start(backend.port, cx));
    let ready = changed.clone();
    let ready_bulk = changed.clone();
    let fed = changed.clone();
    let _main = cx.update(|cx| {
        cx.observe(&main, move |_, _| {
            let _ = ready.unbounded_send(());
        })
    });
    let _bulk = cx.update(|cx| {
        cx.observe(&bulk, move |_, _| {
            let _ = ready_bulk.unbounded_send(());
        })
    });
    let _feed = cx.update(|cx| {
        cx.observe(&feed, move |_, _| {
            let _ = fed.unbounded_send(());
        })
    });
    until("native captured sockets", &mut changes, &changed, || {
        cx.update(|cx| {
            main.read(cx).connection_generation().is_some()
                && bulk.read(cx).connection_generation().is_some()
        })
    })
    .await;
    let watch = cx.update(|cx| {
        feed.update(cx, |feed, cx| {
            feed.watch_native(parent("a"), source("a", 7), cx).unwrap()
        })
    });
    until(
        "first stable-cut page is held",
        &mut changes,
        &changed,
        || pages.lock().unwrap().len() == 1,
    )
    .await;
    let push = cx.update(|cx| {
        main.read(cx)
            .call::<Value>("fixture", "push", json!({}), None)
    });
    assert!(matches!(push.await, Outcome::Ok(_)));
    cx.run_until_parked();
    assert_eq!(
        pages.lock().unwrap().len(),
        1,
        "pushes cannot start a concurrent pass"
    );
    let release = cx.update(|cx| {
        bulk.read(cx)
            .call::<Value>("fixture", "release_first", json!({}), None)
    });
    assert!(matches!(release.await, Outcome::Ok(_)));
    until("opaque second page is held", &mut changes, &changed, || {
        pages.lock().unwrap().len() == 2
    })
    .await;
    cx.update(|cx| {
        assert!(
            feed.read(cx).subagents_of(&parent("a")).is_empty(),
            "partial pass must remain invisible"
        )
    });
    assert!(!watch.loaded());
    let release = cx.update(|cx| {
        bulk.read(cx)
            .call::<Value>("fixture", "release_second", json!({}), None)
    });
    assert!(matches!(release.await, Outcome::Ok(_)));
    until(
        "one coalesced following pass",
        &mut changes,
        &changed,
        || {
            cx.update(|cx| {
                feed.read(cx)
                    .subagents_of(&parent("a"))
                    .first()
                    .is_some_and(|card| card.seq == 13)
            })
        },
    )
    .await;
    assert!(watch.loaded());
    assert_eq!(pages.lock().unwrap().len(), 3);
}

#[test]
fn persisted_can_stop_is_explicit_and_legacy_running_state_never_grants_it() {
    let legacy = item("subagent:actual-child", 10, "running");
    assert!(!legacy.can_stop);
    let mut value = serde_json::to_value(&legacy).unwrap();
    value["canStop"] = json!(true);
    let allowed: SubagentItem = serde_json::from_value(value.clone()).unwrap();
    assert!(allowed.can_stop);
    value["canStop"] = json!(false);
    assert!(
        !serde_json::from_value::<SubagentItem>(value)
            .unwrap()
            .can_stop
    );
}
