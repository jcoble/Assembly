//! Navigator refusal fixtures use no provider or live data; outgoing state must survive failed preparation.
use super::SessionPane;
use crate::core::engine::SessionKey;
use crate::core::intents::session_open::{SessionOpenRequest, SessionOpenTarget};
use crate::core::state::selected_session::SelectedSession;
use gpui::TestAppContext;

#[gpui::test]
fn an_invalid_reference_never_changes_the_visible_session_or_starts_a_subscription(
    cx: &mut TestAppContext,
) {
    let (pane, client, cx) = super::tests::open(cx);
    cx.update(|_, cx| SelectedSession::init(cx));
    pane.update_in(cx, |pane, window, cx| {
        pane.request_open(
            SessionOpenRequest {
                key: SessionKey {
                    machine: "owner".into(),
                    id: "".into(),
                },
                request_id: "bad".into(),
                target: SessionOpenTarget::Engine,
            },
            window,
            cx,
        )
    });
    cx.run_until_parked();
    cx.update(|_, cx| {
        assert!(pane.read(cx).session.is_none());
        assert!(SelectedSession::global(cx).read(cx).session_id().is_none());
    });
    assert!(client.subscribed().is_empty());
    assert!(client.calls().is_empty());
}

#[gpui::test]
fn a_valid_request_without_a_live_captured_connection_preserves_outgoing_rows(
    cx: &mut TestAppContext,
) {
    let (pane, client, cx) = super::tests::open(cx);
    cx.update(|_, cx| SelectedSession::init(cx));
    pane.update_in(cx, |pane, window, cx| {
        pane.session = Some("outgoing".into());
        pane.request_open(
            SessionOpenRequest {
                key: SessionKey {
                    machine: "owner".into(),
                    id: "target".into(),
                },
                request_id: "open".into(),
                target: SessionOpenTarget::Historical,
            },
            window,
            cx,
        );
    });
    cx.run_until_parked();
    cx.update(|_, cx| assert_eq!(pane.read(cx).session.as_deref(), Some("outgoing")));
    assert!(client.subscribed().is_empty());
    assert!(client.calls().is_empty());
}

#[gpui::test]
fn qualified_selection_changes_even_when_two_owners_use_the_same_session_id(
    cx: &mut TestAppContext,
) {
    cx.update(|cx| {
        SelectedSession::init(cx);
        let selected = SelectedSession::global(cx);
        selected.update(cx, |selected, cx| {
            selected.commit_open(
                SessionKey {
                    machine: "first".into(),
                    id: "same".into(),
                },
                crate::core::socket::row_source::ExpectedRowSource {
                    machine: "first".into(),
                    generation: 3,
                    route_machine: Some("first route".into()),
                },
                false,
                cx,
            )
        });
        selected.update(cx, |selected, cx| {
            selected.commit_open(
                SessionKey {
                    machine: "second".into(),
                    id: "same".into(),
                },
                crate::core::socket::row_source::ExpectedRowSource {
                    machine: "second".into(),
                    generation: 7,
                    route_machine: Some("second route".into()),
                },
                true,
                cx,
            )
        });
        assert_eq!(selected.read(cx).key().unwrap().machine, "second");
        assert!(selected.read(cx).read_only());
    });
}

#[gpui::test]
fn readonly_composer_deactivation_starts_no_config_pending_watch_or_write(cx: &mut TestAppContext) {
    use crate::features::session::view::session_action::SessionAction;
    let (pane, client, cx) = super::tests::open(cx);
    pane.update_in(cx, |pane, window, cx| {
        pane.composer.update(cx, |composer, cx| {
            composer.set_session(Some("outgoing".into()), window, cx)
        })
    });
    cx.run_until_parked();
    let before = (
        client.config_calls().len(),
        client.engine_calls().len(),
        client.calls().len(),
    );
    pane.update_in(cx, |pane, window, cx| {
        pane.read_only = true;
        pane.session = Some("history".into());
        pane.composer.update(cx, |composer, cx| {
            composer.set_target(None, None, window, cx)
        });
        for action in [
            SessionAction::RetryPrompt {
                client_id: "p".into(),
            },
            SessionAction::Answer {
                ask_id: "ask".into(),
                answer: serde_json::Value::Null,
            },
            SessionAction::StopTask {
                task: "native".into(),
            },
        ] {
            pane.on_action(action, window, cx);
        }
    });
    cx.run_until_parked();
    assert_eq!(
        (
            client.config_calls().len(),
            client.engine_calls().len(),
            client.calls().len()
        ),
        before
    );
    assert_eq!(client.message_subscriptions(), 0);
}

struct RefusingChatRestore;
impl crate::core::state::workspace_panes::PaneCodec for RefusingChatRestore {
    fn name(&self) -> &'static str {
        "chat"
    }
    fn capture(
        &self,
        _: &gpui_kit::Window,
        _: &gpui_kit::App,
    ) -> Result<serde_json::Value, String> {
        Ok(serde_json::json!({"anchor":"actual-outgoing"}))
    }
    fn validate(
        &self,
        _: &serde_json::Value,
        _: &crate::core::state::workspace_panes::WorkspaceKey,
        _: &serde_json::Value,
        _: &gpui_kit::App,
    ) -> Result<(), String> {
        Ok(())
    }
    fn restore(&self, _: &serde_json::Value, _: &mut gpui_kit::Window, cx: &mut gpui_kit::App) {
        crate::core::state::workspace_panes::WorkspacePanes::refuse_restore(
            "actual fixture codec failure".into(),
            cx,
        );
    }
}

#[gpui::test]
async fn a_real_post_commit_codec_failure_reports_refusal_with_the_target_transcript_visible(
    cx: &mut TestAppContext,
) {
    use crate::core::intents::session_open::SessionOpenResult;
    use crate::core::socket::{
        client::SocketClient,
        endpoint::Endpoint,
        fake_backend::{FakeBackend, Reply},
        test_wait::until,
    };
    use crate::core::state::workspace_panes::WorkspacePanes;
    use crate::features::session::client::socket_session_client::SocketSessionClient;
    use crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator;
    use futures_channel::mpsc::unbounded;
    use serde_json::json;
    use std::{cell::RefCell, rc::Rc};
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
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
            (Some("sub"), _, _, Some("rows")) => vec![Reply::Frame(
                json!({"t":"state","sub":id,"seq":1,"items":[]}),
            )],
            (Some("call"), Some("rows"), Some("page"), _) => vec![Reply::Frame(
                json!({"t":"result","id":id,"result":{"rows":[{"sessionId":"target","seq":1,"itemId":"u","runId":null,"kind":"user","payload":{"text":"readable target transcript"},"createdAt":1}],"hasBefore":false,"activeRunId":null}}),
            )],
            (Some("call"), Some("workspaces"), Some("read"), _) => vec![Reply::Frame(
                json!({"t":"result","id":id,"result":{"revision":7,"snapshot":{"version":1,"chat":{"anchor":"target"}}}}),
            )],
            _ => vec![],
        }
    });
    cx.executor().allow_parking();
    let (main, bulk) = cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        WorkspacePanes::register(Box::new(RefusingChatRestore), cx).unwrap();
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
        pane.request_open(
            SessionOpenRequest {
                key: SessionKey {
                    machine: "owner".into(),
                    id: "target".into(),
                },
                request_id: "restore-failure".into(),
                target: SessionOpenTarget::Historical,
            },
            window,
            cx,
        )
    });
    until(
        "correlated post-commit restore failure",
        &mut changes,
        &changed,
        || !outcomes.borrow().is_empty(),
    )
    .await;
    let results = outcomes.borrow();
    assert_eq!(results.len(), 1);
    assert_eq!(results[0].request_id, "restore-failure");
    let error = results[0].result.as_ref().unwrap_err();
    assert_eq!(error.code, "workspace_restore_failed");
    assert!(error.message.contains("actual fixture codec failure"));
    cx.update(|_, cx| {
        let pane = pane.read(cx);
        assert_eq!(pane.session.as_deref(), Some("target"));
        assert_eq!(pane.view.items.len(), 1);
        assert_eq!(
            SelectedSession::global(cx).read(cx).key(),
            Some(&SessionKey {
                machine: "owner".into(),
                id: "target".into()
            })
        );
    });
}
