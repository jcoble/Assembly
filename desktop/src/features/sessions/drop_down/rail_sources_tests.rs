//! Actual nullable route discovery is separate from SQL rail paging and never names an Engine by its saved route.
use super::rail_sources::routes;
use crate::core::state::machine::Machine;
use crate::features::session::client::source_resolution;
use serde_json::json;
#[test]
fn online_remote_sources_outside_the_current_rail_page_are_discovered_by_explicit_routes() {
    let remote: Machine = serde_json::from_value(
        json!({"name":"Saved B","kind":"remote","reachable":true,"checked":true}),
    )
    .unwrap();
    let offline: Machine = serde_json::from_value(
        json!({"name":"Offline","kind":"remote","reachable":false,"checked":true}),
    )
    .unwrap();
    assert_eq!(
        routes([&remote, &offline]),
        vec![None, Some("Saved B".into())]
    );
    let actual = source_resolution::parse_reply(
        json!({"machine":"actual-B","generation":7,"routeMachine":"Saved B"}),
        Some("Saved B"),
        Some("actual-A"),
    )
    .unwrap();
    assert_eq!(actual.machine, "actual-B");
    assert!(
        source_resolution::parse_reply(
            json!({"machine":"Saved B","generation":0,"routeMachine":"Saved B"}),
            Some("Saved B"),
            Some("actual-A")
        )
        .is_err()
    );
    assert_eq!(
        source_resolution::build_args(None).unwrap(),
        json!({"routeMachine":null})
    );
}

// Source-derived real socket fixture, not native/provider acceptance.
#[gpui::test]
async fn remote_child_outside_the_sql_rail_page_uses_the_one_discovered_source_scope(
    cx: &mut gpui::TestAppContext,
) {
    use crate::core::{
        engine::{EngineFeed, SessionKey},
        socket::{
            client::SocketClient,
            endpoint::Endpoint,
            fake_backend::{FakeBackend, Reply},
            test_wait::until,
        },
        state::{machines::Machines, selected_session::SelectedSession},
    };
    use crate::features::sessions::sessions_pane::SessionsPane;
    use futures_channel::mpsc::unbounded;
    let backend = FakeBackend::start(|frame| {
        let id = frame["id"].clone();
        let result = |value| vec![Reply::Frame(json!({"t":"result","id":id,"result":value}))];
        match (
            frame["t"].as_str(),
            frame["topic"].as_str(),
            frame["method"].as_str(),
        ) {
            (Some("hello"), ..) => vec![Reply::Frame(
                json!({"t":"welcome","protocol":1,"machine":"Friendly A","engine":"actual-A"}),
            )],
            (Some("sub"), Some("machines"), _) => vec![Reply::Frame(
                json!({"t":"state","sub":id,"items":[{"key":"Saved B","item":{"name":"Saved B","kind":"remote","reachable":true,"checked":true}}]}),
            )],
            (Some("call"), _, Some("resolve_source")) => {
                let remote = frame["args"]["routeMachine"] == "Saved B";
                result(
                    json!({"machine":if remote{"actual-B"}else{"actual-A"},"generation":if remote{7}else{0},"routeMachine":if remote{Some("Saved B")}else{None}}),
                )
            }
            (Some("sub"), Some("sessions"), _) if frame["stream"] == "engine" => {
                let remote = frame["machine"] == "actual-B";
                let owner = if remote { "actual-B" } else { "actual-A" };
                let row = json!({"entityKind":"session","machine":owner,"id":if remote{"child-off-page"}else{"parent"},"version":1,"parentId":if remote{Some("parent")}else{None},"parentMachine":if remote{Some("actual-A")}else{None},"provider":"fixture","folder":"/recorded","state":"idle","activityState":"idle","epoch":1});
                vec![Reply::Frame(
                    json!({"t":"engine_snapshot","id":id,"machine":owner,"through":1,"items":[row],"more":false}),
                )]
            }
            (Some("sub"), Some("needs_me"), _) if frame["stream"] == "engine" => {
                vec![Reply::Frame(
                    json!({"t":"engine_snapshot","id":id,"machine":frame["machine"],"through":1,"items":[],"more":false}),
                )]
            }
            (Some("call"), _, _) => result(json!({})),
            _ => vec![],
        }
    });
    cx.executor().allow_parking();
    let (socket, feed) = cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        let socket = SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "fixture".into(),
            },
            cx,
        );
        Machines::start(cx);
        (socket, EngineFeed::global(cx))
    });
    let (pane, visual) = cx.add_window_view(SessionsPane::new);
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let connected = changed.clone();
    let _feed = visual.update(|_, cx| {
        cx.observe(&feed, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    let _socket = visual.update(|_, cx| {
        cx.observe(&socket, move |_, _| {
            let _ = connected.unbounded_send(());
        })
    });
    let parent = SessionKey {
        machine: "actual-A".into(),
        id: "parent".into(),
    };
    until(
        "remote child outside empty rail",
        &mut changes,
        &changed,
        || visual.update(|_, cx| feed.read(cx).children_of(&parent).len() == 1),
    )
    .await;
    visual.update(|_, cx| {
        assert!(pane.read(cx).rail.sessions.is_empty());
        let children = feed.read(cx).children_of(&parent);
        assert_eq!(children[0].key.machine, "actual-B");
        assert_eq!(children[0].key.id, "child-off-page");
        assert_eq!(
            pane.read(cx).engine_watches.len(),
            4,
            "one shared sessions/needs lease per actual source"
        );
    });
    drop(pane);
}

// Source-derived real socket fixture, not native/provider acceptance.
#[gpui::test]
async fn a_saved_machine_that_stops_answering_keeps_its_rows_until_it_returns_as_another_engine(
    cx: &mut gpui::TestAppContext,
) {
    use crate::core::{
        engine::EngineFeed,
        socket::{
            client::SocketClient,
            endpoint::Endpoint,
            fake_backend::{FakeBackend, Reply},
            test_wait::until,
        },
        state::{machines::Machines, selected_session::SelectedSession},
    };
    use crate::features::sessions::sessions_pane::SessionsPane;
    use futures_channel::mpsc::unbounded;
    use serde_json::Value;
    let machine = |reachable: bool| json!({"name":"Saved B","kind":"remote","reachable":reachable,"checked":true});
    let mut machines_sub = Value::Null;
    let mut remote = "actual-B";
    let backend = FakeBackend::start(move |frame| {
        let id = frame["id"].clone();
        let result = |value| vec![Reply::Frame(json!({"t":"result","id":id,"result":value}))];
        match (
            frame["t"].as_str(),
            frame["topic"].as_str(),
            frame["method"].as_str(),
        ) {
            (Some("hello"), ..) => vec![Reply::Frame(
                json!({"t":"welcome","protocol":1,"machine":"Friendly A","engine":"actual-A"}),
            )],
            (Some("sub"), Some("machines"), _) => {
                machines_sub = id.clone();
                vec![Reply::Frame(
                    json!({"t":"state","sub":id,"items":[{"key":"Saved B","item":machine(true)}]}),
                )]
            }
            (Some("call"), _, Some(change @ ("machine_down" | "machine_back"))) => {
                let back = change == "machine_back";
                if back {
                    remote = "actual-B-reinstalled";
                }
                let mut replies = vec![Reply::Frame(
                    json!({"t":"change","sub":machines_sub,"key":"Saved B","item":machine(back)}),
                )];
                replies.extend(result(json!({})));
                replies
            }
            (Some("call"), _, Some("resolve_source")) => {
                let saved = frame["args"]["routeMachine"] == "Saved B";
                result(
                    json!({"machine":if saved{remote}else{"actual-A"},"generation":if saved{7}else{0},"routeMachine":if saved{Some("Saved B")}else{None}}),
                )
            }
            (Some("sub"), Some("sessions" | "needs_me"), _) if frame["stream"] == "engine" => {
                vec![Reply::Frame(
                    json!({"t":"engine_snapshot","id":id,"machine":frame["machine"],"through":1,"items":[],"more":false}),
                )]
            }
            (Some("call"), _, _) => result(json!({})),
            _ => vec![],
        }
    });
    cx.executor().allow_parking();
    let (socket, feed, listed) = cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        let socket = SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "fixture".into(),
            },
            cx,
        );
        Machines::start(cx);
        (
            socket,
            EngineFeed::global(cx),
            Machines::try_global(cx).unwrap(),
        )
    });
    let (pane, visual) = cx.add_window_view(SessionsPane::new);
    let (changed, mut changes) = unbounded();
    let (tell, told) = (changed.clone(), changed.clone());
    let _feed = visual.update(|_, cx| {
        cx.observe(&feed, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    let _machines = visual.update(|_, cx| {
        cx.observe(&listed, move |_, _| {
            let _ = told.unbounded_send(());
        })
    });
    let loaded = |visual: &mut gpui::VisualTestContext| {
        visual.update(|_, cx| feed.read(cx).loaded_machines("sessions"))
    };
    let both = vec!["actual-A".to_owned(), "actual-B".to_owned()];
    until("the remote's rows", &mut changes, &changed, || {
        loaded(visual) == both
    })
    .await;
    let change = |visual: &mut gpui::VisualTestContext, method: &str| {
        visual.update(|_, cx| {
            socket
                .read(cx)
                .call::<Value>("test", method, json!({}), None)
        })
    };
    change(visual, "machine_down").await;
    until(
        "the machine to read offline",
        &mut changes,
        &changed,
        || visual.update(|_, cx| listed.read(cx).machines().any(|held| !held.reachable)),
    )
    .await;
    assert_eq!(
        loaded(visual),
        both,
        "the Board still needs the offline machine's rows"
    );
    change(visual, "machine_back").await;
    let replaced = vec!["actual-A".to_owned(), "actual-B-reinstalled".to_owned()];
    until(
        "only the machine's current engine",
        &mut changes,
        &changed,
        || loaded(visual) == replaced,
    )
    .await;
    drop(pane);
}

// Source-derived real socket fixtures, not native/provider acceptance. No rail state or preference frame is answered
// and no needs_me snapshot arrives, so the session notice under test is the only thing that can read the rail.
mod session_notices {
    use crate::core::{
        engine::{EngineFeed, EngineKey},
        socket::{
            client::SocketClient,
            endpoint::Endpoint,
            fake_backend::{FakeBackend, Reply},
            test_wait::until,
        },
        state::{machines::Machines, selected_session::SelectedSession},
    };
    use crate::features::sessions::sessions_pane::SessionsPane;
    use futures_channel::mpsc::unbounded;
    use gpui_kit::{Entity, VisualTestContext};
    use serde_json::{Value, json};
    use std::sync::atomic::{AtomicBool, Ordering::SeqCst};
    const ROW: &str = "new-remote";
    fn result(id: &Value, value: Value) -> Vec<Reply> {
        vec![Reply::Frame(json!({"t":"result","id":id,"result":value}))]
    }
    fn snapshot(id: &Value, through: i64) -> Vec<Reply> {
        vec![Reply::Frame(
            json!({"t":"engine_snapshot","id":id,"machine":"actual-B","through":through,"items":[],"more":false}),
        )]
    }
    fn remote_sessions(frame: &Value) -> bool {
        frame["t"] == "sub"
            && frame["topic"] == "sessions"
            && frame["stream"] == "engine"
            && frame["machine"] == "actual-B"
    }
    /// The entity read that hands over the session's display row.
    fn entities(frame: &Value) -> Vec<Reply> {
        result(
            &frame["id"],
            json!({"items":[{"machine":"actual-B","entityKind":"session","id":ROW,"version":2,"title":"New remote","state":"working"}]}),
        )
    }
    /// One remote source; the SQL rail holds the row only once `stream` says the Engine has it.
    fn backend(
        mut stream: impl FnMut(&Value, &AtomicBool) -> Vec<Reply> + Send + 'static,
    ) -> FakeBackend {
        let held = AtomicBool::new(false);
        FakeBackend::start(move |frame| {
            match (
                frame["t"].as_str(),
                frame["topic"].as_str(),
                frame["method"].as_str(),
            ) {
                (Some("hello"), ..) => vec![Reply::Frame(
                    json!({"t":"welcome","protocol":1,"machine":"Friendly A","engine":"actual-A"}),
                )],
                (Some("sub"), Some("machines"), _) => vec![Reply::Frame(
                    json!({"t":"state","sub":frame["id"],"items":[{"key":"Saved B","item":{"name":"Saved B","kind":"remote","reachable":true,"checked":true}}]}),
                )],
                (Some("call"), _, Some("resolve_source")) => {
                    let remote = frame["args"]["routeMachine"] == "Saved B";
                    result(
                        &frame["id"],
                        json!({"machine":if remote{"actual-B"}else{"actual-A"},"generation":if remote{7}else{0},"routeMachine":if remote{Some("Saved B")}else{None}}),
                    )
                }
                (Some("call"), _, Some("rail")) => {
                    let row = json!({"sessionId":ROW,"kind":"session","section":"working","agent":"claude","repoUrl":"r","group":"r","count":0,"machines":[],"title":null,"machine":"actual-B","updatedAt":1,"completedAt":null,"running":false,"failed":false,"unread":false,"ordering":ROW,"branch":null,"icon":"icons/bot.svg","sectionIndex":0,"header":2});
                    result(
                        &frame["id"],
                        json!({"totals":{"needs":0},"agents":[],"sessions":if held.load(SeqCst){vec![row]}else{vec![]},"next":null,"start":0}),
                    )
                }
                _ => stream(frame, &held),
            }
        })
    }
    fn open<'a>(
        backend: &FakeBackend,
        cx: &'a mut gpui::TestAppContext,
    ) -> (
        Entity<SessionsPane>,
        Entity<SocketClient>,
        &'a mut VisualTestContext,
    ) {
        cx.executor().allow_parking();
        let bulk = cx.update(|cx| {
            gpui_kit::init(cx);
            SelectedSession::init(cx);
            let endpoint = Endpoint {
                port: backend.port,
                token: "fixture".into(),
            };
            SocketClient::start(endpoint.clone(), cx);
            let bulk = SocketClient::start_bulk(endpoint, cx);
            Machines::start(cx);
            bulk
        });
        let (pane, visual) = cx.add_window_view(SessionsPane::new);
        (pane, bulk, visual)
    }
    async fn rail_shows_the_row(pane: &Entity<SessionsPane>, visual: &mut VisualTestContext) {
        let (changed, mut changes) = unbounded();
        let tell = changed.clone();
        let _heard = visual.update(|_, cx| {
            cx.observe(pane, move |_, _| {
                let _ = tell.unbounded_send(());
            })
        });
        until(
            "the rail read again after a session notice",
            &mut changes,
            &changed,
            || {
                visual.update(|_, cx| {
                    pane.read(cx)
                        .rail
                        .sessions
                        .iter()
                        .any(|row| row.session_id == ROW)
                })
            },
        )
        .await;
    }
    #[gpui::test]
    async fn a_remote_session_event_reads_the_rail_again(cx: &mut gpui::TestAppContext) {
        let backend = backend(|frame, held| {
            if remote_sessions(frame) {
                return snapshot(&frame["id"], 1);
            }
            if frame["t"] == "ack" && frame["after"] == 1 {
                return vec![Reply::Frame(
                    json!({"t":"engine_events","id":frame["id"],"machine":"actual-B","after":1,"through":2,"events":[{"machine":"actual-B","seq":2,"entityKind":"session","entityId":ROW,"version":2}]}),
                )];
            }
            if frame["method"] == "entities" {
                held.store(true, SeqCst);
                return entities(frame);
            }
            vec![]
        });
        let (pane, _bulk, visual) = open(&backend, cx);
        rail_shows_the_row(&pane, visual).await;
    }
    /// A mirror reset's replacement cut: the scope's notice carries no keys when nothing was held on either side.
    #[gpui::test]
    async fn a_replacement_snapshot_with_no_keys_reads_the_rail_again(
        cx: &mut gpui::TestAppContext,
    ) {
        let backend = backend(|frame, held| {
            if remote_sessions(frame) {
                return snapshot(&frame["id"], 1);
            }
            if frame["t"] == "ack" && frame["after"] == 1 {
                held.store(true, SeqCst);
                return snapshot(&frame["id"], 2);
            }
            vec![]
        });
        let (pane, _bulk, visual) = open(&backend, cx);
        rail_shows_the_row(&pane, visual).await;
    }
    /// A checked read of one session: its notice has the session key and no scope. No stream here answers.
    #[gpui::test]
    async fn a_directly_read_session_reads_the_rail_again(cx: &mut gpui::TestAppContext) {
        let backend = backend(|frame, held| {
            if frame["method"] == "entities" {
                held.store(true, SeqCst);
                return entities(frame);
            }
            vec![]
        });
        let (pane, bulk, visual) = open(&backend, cx);
        let (changed, mut changes) = unbounded();
        let tell = changed.clone();
        let _heard = visual.update(|_, cx| {
            cx.observe(&bulk, move |_, _| {
                let _ = tell.unbounded_send(());
            })
        });
        until("the bulk socket", &mut changes, &changed, || {
            visual.update(|_, cx| bulk.read(cx).connection_generation().is_some())
        })
        .await;
        let feed = visual.update(|_, cx| EngineFeed::global(cx));
        let read = visual.update(|_, cx| {
            feed.update(cx, |feed, cx| {
                feed.ensure_item(
                    EngineKey {
                        machine: "actual-B".into(),
                        kind: "session".into(),
                        id: ROW.into(),
                    },
                    cx,
                )
            })
        });
        assert!(matches!(read.await, Ok(Some(_))));
        rail_shows_the_row(&pane, visual).await;
    }
}
