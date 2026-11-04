//! Session open tests for source refusals that arrive while the first page is being prepared or the workspace read is held.
use super::SessionPane;
use crate::core::engine::SessionKey;
use crate::core::intents::session_open::{SessionOpenRequest, SessionOpenTarget};
use crate::core::state::selected_session::SelectedSession;
use gpui::TestAppContext;

// Fake wire fixture: this proves client cancellation/correlation, never backend native authority.
#[gpui::test]
async fn a_source_refusal_on_the_prepared_first_page_preserves_the_outgoing_view(
    cx: &mut TestAppContext,
) {
    use crate::core::intents::session_open::SessionOpenResult;
    use crate::core::socket::{
        client::SocketClient,
        endpoint::Endpoint,
        fake_backend::{FakeBackend, Reply},
        test_wait::until,
    };
    use crate::features::session::client::socket_session_client::SocketSessionClient;
    use futures_channel::mpsc::unbounded;
    use serde_json::json;
    use std::{
        cell::RefCell,
        rc::Rc,
        sync::{Arc, Mutex},
    };
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let seen = Arc::new(Mutex::new(Vec::new()));
    let wire = seen.clone();
    let backend = FakeBackend::start(move |frame| {
        wire.lock().unwrap().push(frame.clone());
        let _ = tell.unbounded_send(());
        let id = &frame["id"];
        match (
            frame["t"].as_str(),
            frame["method"].as_str(),
            frame["topic"].as_str(),
        ) {
            (Some("hello"), ..) => vec![Reply::Frame(
                json!({"t":"welcome","protocol":1,"machine":"box","engine":"owner"}),
            )],
            (Some("call"), Some("open_target"), _) => {
                vec![Reply::Frame(json!({"t":"result","id":id,"result":{
                    "kind":"historical","machine":"owner","generation":0,"routeMachine":null,"sessionId":"target",
                    "parentSessionId":null,"itemId":null,"seq":null,"payloadSessionId":null,"readOnly":true,"hasHistoryRows":true,
                    "originalKey":{"machine":"owner","id":"target"},"requestId":frame["args"]["requestId"],"checkoutRoot":null,
                    "expectedSource":{"machine":"owner","generation":0,"routeMachine":null},"transcriptOnly":true
                }}))]
            }
            (Some("sub"), _, Some("rows")) => vec![Reply::Frame(
                json!({"t":"state","sub":id,"seq":7,"items":[]}),
            )],
            (Some("call"), Some("page"), _) => vec![Reply::Frame(
                json!({"t":"error","id":id,"code":"source_changed","message":"actual fixture source refusal"}),
            )],
            _ => vec![],
        }
    });
    cx.executor().allow_parking();
    let (main, bulk) = cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        (
            SocketClient::start(endpoint.clone(), cx),
            SocketClient::start_bulk(endpoint, cx),
        )
    });
    let ready_tell = changed.clone();
    let bulk_tell = changed.clone();
    let _main = cx.update(|cx| {
        cx.observe(&main, move |_, _| {
            let _ = ready_tell.unbounded_send(());
        })
    });
    let _bulk = cx.update(|cx| {
        cx.observe(&bulk, move |_, _| {
            let _ = bulk_tell.unbounded_send(());
        })
    });
    until(
        "both captured socket generations",
        &mut changes,
        &changed,
        || {
            cx.update(|cx| {
                main.read(cx).connection_generation().is_some()
                    && bulk.read(cx).connection_generation().is_some()
            })
        },
    )
    .await;
    let (pane, cx) =
        cx.add_window_view(|window, cx| SessionPane::new(Rc::new(SocketSessionClient), window, cx));
    let results = Rc::new(RefCell::new(Vec::new()));
    let captured = results.clone();
    let result_tell = changed.clone();
    let _results = cx.update(|_, cx| {
        cx.subscribe(
            &SelectedSession::global(cx),
            move |_, event: &SessionOpenResult, _| {
                captured.borrow_mut().push(event.clone());
                let _ = result_tell.unbounded_send(());
            },
        )
    });
    pane.update_in(cx, |pane, window, cx| {
        pane.session = Some("outgoing".into());
        pane.request_open(
            SessionOpenRequest {
                key: SessionKey {
                    machine: "owner".into(),
                    id: "target".into(),
                },
                request_id: "correlated".into(),
                target: SessionOpenTarget::Historical,
            },
            window,
            cx,
        );
    });
    until(
        "the exact source refusal result",
        &mut changes,
        &changed,
        || !results.borrow().is_empty(),
    )
    .await;
    let result = results.borrow();
    assert_eq!(result.len(), 1);
    assert_eq!(result[0].request_id, "correlated");
    assert_eq!(
        result[0].result.as_ref().unwrap_err().code,
        "source_changed"
    );
    cx.update(|_, cx| {
        let pane = pane.read(cx);
        assert_eq!(pane.session.as_deref(), Some("outgoing"));
        assert!(pane.pending_open.is_none());
    });
    let seen = seen.lock().unwrap();
    let source = json!({"machine":"owner","generation":0,"routeMachine":null});
    assert_eq!(
        seen.iter()
            .find(|frame| frame["t"] == "sub" && frame["topic"] == "rows")
            .unwrap()["expectedSource"],
        source
    );
    assert_eq!(
        seen.iter().find(|frame| frame["method"] == "page").unwrap()["args"]["expectedSource"],
        source
    );
}

#[gpui::test]
fn a_refused_captured_source_cannot_start_more_detail_or_write_calls(cx: &mut TestAppContext) {
    use crate::core::socket::row_source::ExpectedRowSource;
    use crate::features::session::view::session_action::SessionAction;
    let (pane, client, cx) = super::tests::open(cx);
    pane.update_in(cx, |pane, window, cx| {
        pane.session = Some("history".into());
        pane.row_source = Some(ExpectedRowSource {
            machine: "owner".into(),
            generation: 5,
            route_machine: Some("captured".into()),
        });
        pane.on_rows(
            crate::features::session::client::session_client::RowUpdate::Refused {
                code: "source_changed".into(),
                message: "The captured owner changed.".into(),
            },
            cx,
        );
        pane.on_action(
            SessionAction::ShowImage {
                name: "image".into(),
            },
            window,
            cx,
        );
        pane.on_action(
            SessionAction::RetryPrompt {
                client_id: "prompt".into(),
            },
            window,
            cx,
        );
    });
    cx.run_until_parked();
    assert!(client.calls().is_empty());
    assert!(client.config_calls().is_empty());
    assert!(client.engine_calls().is_empty());
}

#[gpui::test]
async fn a_source_refusal_while_workspace_read_is_held_invalidates_the_ready_page(
    cx: &mut TestAppContext,
) {
    use crate::core::intents::session_open::SessionOpenResult;
    use crate::core::socket::{
        client::SocketClient,
        endpoint::Endpoint,
        fake_backend::{FakeBackend, Reply},
        test_wait::until,
    };
    use crate::features::session::client::socket_session_client::SocketSessionClient;
    use crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator;
    use futures_channel::{mpsc::unbounded, oneshot};
    use serde_json::json;
    use std::{
        cell::RefCell,
        rc::Rc,
        sync::{Arc, Mutex},
    };
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let (held, workspace_read) = oneshot::channel();
    let held = Arc::new(Mutex::new(Some(held)));
    let mut rows_id = None;
    let mut workspace_id = None;
    let backend = FakeBackend::start(move |frame| {
        let _ = tell.unbounded_send(());
        let id = &frame["id"];
        match (
            frame["t"].as_str(),
            frame["feature"].as_str(),
            frame["method"].as_str(),
            frame["topic"].as_str(),
        ) {
            (Some("hello"), ..) => vec![Reply::Frame(
                json!({"t":"welcome","protocol":1,"machine":"box","engine":"owner"}),
            )],
            (Some("call"), Some("sessions"), Some("open_target"), _) => {
                vec![Reply::Frame(json!({"t":"result","id":id,"result":{
                "kind":"historical","machine":"owner","generation":0,"routeMachine":null,"sessionId":"target","parentSessionId":null,
                "itemId":null,"seq":null,"payloadSessionId":null,"readOnly":true,"hasHistoryRows":true,"originalKey":{"machine":"owner","id":"target"},
                "requestId":frame["args"]["requestId"],"checkoutRoot":"/recorded","expectedSource":{"machine":"owner","generation":0,"routeMachine":null},"transcriptOnly":false}}))]
            }
            (Some("sub"), _, _, Some("rows")) => {
                rows_id = Some(id.clone());
                vec![Reply::Frame(
                    json!({"t":"state","sub":id,"seq":1,"items":[]}),
                )]
            }
            (Some("call"), Some("rows"), Some("page"), _) => vec![Reply::Frame(
                json!({"t":"result","id":id,"result":{"rows":[{"sessionId":"target","seq":1,"itemId":"u","runId":null,"kind":"user","payload":{"text":"prepared target"},"createdAt":1}],"hasBefore":false,"activeRunId":null}}),
            )],
            (Some("call"), Some("workspaces"), Some("read"), _) => {
                workspace_id = Some(id.clone());
                if let Some(held) = held.lock().unwrap().take() {
                    let _ = held.send(());
                }
                vec![]
            }
            // Fixture control on the existing main socket releases the actual captured watch's error.
            (Some("call"), Some("fixture"), Some("invalidate"), _) => vec![
                Reply::Frame(
                    json!({"t":"error","id":rows_id,"code":"source_changed","message":"The captured source changed during the workspace read."}),
                ),
                Reply::Frame(json!({"t":"result","id":id,"result":null})),
            ],
            (Some("cancel"), ..) => {
                if workspace_id.as_ref() == Some(id) {
                    vec![Reply::Frame(
                        json!({"t":"result","id":id,"result":{"revision":7,"snapshot":{"version":1}}}),
                    )]
                } else {
                    vec![]
                }
            }
            _ => vec![],
        }
    });
    cx.executor().allow_parking();
    let (main, bulk) = cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        WorkspaceCoordinator::init(cx);
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        (
            SocketClient::start(endpoint.clone(), cx),
            SocketClient::start_bulk(endpoint, cx),
        )
    });
    let first = changed.clone();
    let second = changed.clone();
    let _main = cx.update(|cx| {
        cx.observe(&main, move |_, _| {
            let _ = first.unbounded_send(());
        })
    });
    let _bulk = cx.update(|cx| {
        cx.observe(&bulk, move |_, _| {
            let _ = second.unbounded_send(());
        })
    });
    until("captured sockets", &mut changes, &changed, || {
        cx.update(|cx| {
            main.read(cx).connection_generation().is_some()
                && bulk.read(cx).connection_generation().is_some()
        })
    })
    .await;
    let (pane, cx) =
        cx.add_window_view(|window, cx| SessionPane::new(Rc::new(SocketSessionClient), window, cx));
    let outcomes = Rc::new(RefCell::new(Vec::new()));
    let got = outcomes.clone();
    let result_tell = changed.clone();
    let _result = cx.update(|_, cx| {
        cx.subscribe(
            &SelectedSession::global(cx),
            move |_, event: &SessionOpenResult, _| {
                got.borrow_mut().push(event.clone());
                let _ = result_tell.unbounded_send(());
            },
        )
    });
    pane.update_in(cx, |pane, window, cx| {
        pane.session = Some("outgoing".into());
        pane.request_open(
            SessionOpenRequest {
                key: SessionKey {
                    machine: "owner".into(),
                    id: "target".into(),
                },
                request_id: "held".into(),
                target: SessionOpenTarget::Historical,
            },
            window,
            cx,
        );
    });
    workspace_read
        .await
        .expect("the actual workspace target read reached its socket");
    cx.update(|_, cx| {
        assert!(
            pane.read(cx)
                .pending_open
                .as_ref()
                .unwrap()
                .stage
                .as_ref()
                .unwrap()
                .ready()
        )
    });
    let invalidate = cx.update(|_, cx| {
        main.read(cx)
            .call::<serde_json::Value>("fixture", "invalidate", json!({}), None)
    });
    assert!(matches!(
        invalidate.await,
        crate::core::socket::outcome::Outcome::Ok(_)
    ));
    until("the source refusal", &mut changes, &changed, || {
        !outcomes.borrow().is_empty()
    })
    .await;
    cx.run_until_parked();
    let outcomes = outcomes.borrow();
    assert_eq!(outcomes.len(), 1);
    assert_eq!(
        outcomes[0].result.as_ref().unwrap_err().code,
        "source_changed"
    );
    cx.update(|_, cx| {
        assert_eq!(pane.read(cx).session.as_deref(), Some("outgoing"));
        assert!(SelectedSession::global(cx).read(cx).session_id().is_none());
    });
}
