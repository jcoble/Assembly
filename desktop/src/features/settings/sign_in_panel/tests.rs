//! The actual native panel handlers over the existing main/bulk fake socket transport.
use super::{SignInPanel, SignInTarget, SocketClient, connection_identity};
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use futures_channel::{mpsc::unbounded, oneshot};
use gpui::TestAppContext;
use gpui_kit::{Entity, Task, VisualTestContext};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

#[derive(Default)]
struct Server {
    operation: Option<Value>,
    commands: Vec<Value>,
    acks: Vec<Value>,
    sub: u64,
    already_running: bool,
    hold_provider: Option<std::sync::mpsc::Receiver<Vec<Reply>>>,
    provider_reads: usize,
    provider_version: i64,
    seq: i64,
    drop_command: bool,
    cancel_conflict: bool,
}
fn target() -> SignInTarget {
    SignInTarget {
        machine: "owner-engine".into(),
        provider: "codex".into(),
        account_context: "".into(),
        provider_target_id: "codex/".into(),
        configuration_target_id: "opaque-config".into(),
        machine_label: "This Mac".into(),
        provider_name: "Codex".into(),
    }
}
fn provider() -> Value {
    let facts = json!({"provider":"codex","name":"Codex","adapter":"actual-test-adapter","version":"2.5.0","installation":"installed","auth":"needsSignIn","problem":null,"models":[],"methods":[{"methodId":"advertised-method","name":"Provider login","kind":"agent"}],"checkedMs":1});
    json!({"entityKind":"provider","id":"codex/","machine":"owner-engine","version":7,"provider":"codex","accountContext":"","factsJson":facts.to_string()})
}
fn operation(id: &str, version: i64, state: &str) -> Value {
    json!({"entityKind":"sign_in","id":id,"machine":"owner-engine","version":version,"provider":"codex","accountContext":"","methodId":"advertised-method","state":state,"reason":null})
}
fn fixture(server: Arc<Mutex<Server>>) -> FakeBackend {
    FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        let mut server = server.lock().unwrap();
        let id = frame["id"].clone();
        if frame["t"] == "sub" {
            server.sub = id.as_u64().unwrap();
            let mut items = vec![provider()];
            if let Some(row) = &server.operation {
                items.push(row.clone());
            }
            return vec![Reply::Frame(
                json!({"t":"engine_snapshot","id":id,"machine":"owner-engine","through":0,"items":items,"more":false}),
            )];
        }
        if frame["t"] != "call" {
            return vec![];
        }
        if frame["feature"] == "test" && frame["method"] == "invalidate_provider" {
            let after = server.seq;
            server.seq += 1;
            return vec![
                Reply::Frame(
                    json!({"t":"engine_events","id":server.sub,"machine":"owner-engine","after":after,"through":server.seq,
                "events":[{"machine":"owner-engine","seq":server.seq,"kind":"provider.changed","entityKind":"provider","entityId":"codex/","version":8,"atMs":1,"late":false,"data":{}}],"more":false}),
                ),
                Reply::Frame(json!({"t":"result","id":id,"result":null})),
            ];
        }
        if frame["feature"] == "engine"
            && frame["method"] == "entity"
            && frame["args"]["entityKind"] == "provider"
        {
            server.provider_reads += 1;
            if let Some(replies) = server.hold_provider.take() {
                return vec![Reply::Wait(replies)];
            }
        }
        if frame["feature"] == "engine" && frame["method"] == "command" && server.drop_command {
            server.drop_command = false;
            server.commands.push(frame["args"].clone());
            return vec![Reply::Drop];
        }
        let result = match (frame["feature"].as_str(), frame["method"].as_str()) {
            (Some("engine"), Some("machine")) => json!({"machine":"owner-engine","through":0}),
            (Some("engine"), Some("entity")) => {
                if frame["args"]["entityKind"] == "provider" {
                    let mut row = provider();
                    row["version"] = json!(server.provider_version.max(7));
                    row
                } else {
                    server.operation.clone().unwrap_or(Value::Null)
                }
            }
            (Some("engine"), Some("command")) => {
                let args = frame["args"].clone();
                server.commands.push(args.clone());
                if args["command"]["kind"] == "start_sign_in" {
                    if server.already_running {
                        server.operation =
                            Some(operation("actual-other-window", 9, "waiting_for_person"));
                        json!({"applied":false,"late":false,"error":"already_running","version":0,"seq":0,"epoch":0})
                    } else {
                        server.operation = Some(operation(
                            args["entityId"].as_str().unwrap(),
                            1,
                            "waiting_for_person",
                        ));
                        json!({"applied":true,"late":false,"error":null,"version":1,"seq":0,"epoch":0})
                    }
                } else if args["command"]["kind"] == "cancel_sign_in" {
                    if server.cancel_conflict {
                        server.cancel_conflict = false;
                        server.operation = Some(operation(
                            args["entityId"].as_str().unwrap(),
                            10,
                            "waiting_for_person",
                        ));
                        json!({"applied":false,"late":false,"error":"version_conflict","version":10,"seq":0,"epoch":0})
                    } else {
                        server.operation = Some(operation(
                            args["entityId"].as_str().unwrap(),
                            11,
                            "cancelled",
                        ));
                        json!({"applied":true,"late":false,"error":null,"version":11,"seq":0,"epoch":0})
                    }
                } else {
                    json!({"applied":true,"late":false,"error":null,"version":8,"seq":0,"epoch":0})
                }
            }
            (Some("providers"), Some("challenge")) => {
                json!({"operationId":frame["args"]["operationId"],"generation":1,"link":"https://example.invalid/login?state=CANARY%2fExact+URL","callbackHost":null,"callbackPort":null,"wantsCode":false,"terminalId":null})
            }
            (Some("providers"), Some("acknowledgeUrl")) => {
                server.acks.push(frame["args"].clone());
                json!({})
            }
            _ => Value::Null,
        };
        vec![Reply::Frame(json!({"t":"result","id":id,"result":result}))]
    })
}
async fn opened(
    cx: &mut TestAppContext,
    server: Arc<Mutex<Server>>,
) -> (FakeBackend, Entity<SignInPanel>, &mut VisualTestContext) {
    cx.executor().allow_parking();
    cx.update(gpui_kit::init);
    let backend = fixture(server);
    let endpoint = Endpoint {
        port: backend.port,
        token: "secret".into(),
    };
    let (main, bulk) = cx.update(|cx| {
        (
            SocketClient::start(endpoint.clone(), cx),
            SocketClient::start_bulk(endpoint, cx),
        )
    });
    let (tell, mut changes) = unbounded();
    let a = tell.clone();
    let b = tell.clone();
    let (_a, _b) = cx.update(|cx| {
        (
            cx.observe(&main, move |_, _| {
                _ = a.unbounded_send(());
            }),
            cx.observe(&bulk, move |_, _| {
                _ = b.unbounded_send(());
            }),
        )
    });
    until("both owner sockets", &mut changes, &tell, || {
        cx.read(|cx| connection_identity(cx).is_some())
    })
    .await;
    let (panel, cx) = cx.add_window_view(|window, cx| SignInPanel::new(target(), window, cx));
    settled(&panel, cx, |panel| {
        panel.projection.ready && panel.projection.provider.is_some() && panel._provider.is_none()
    })
    .await;
    (backend, panel, cx)
}
async fn settled(
    panel: &Entity<SignInPanel>,
    cx: &mut VisualTestContext,
    done: impl Fn(&SignInPanel) -> bool,
) {
    let (tell, mut changes) = unbounded();
    let signal = tell.clone();
    let _observe = cx.update(|_, cx| {
        cx.observe(panel, move |_, _| {
            _ = signal.unbounded_send(());
        })
    });
    until("panel", &mut changes, &tell, || {
        panel.read_with(cx, |panel, _| done(panel))
    })
    .await;
}

#[gpui::test]
async fn actual_handlers_send_advertised_identity_and_current_versions(cx: &mut TestAppContext) {
    let server = Arc::new(Mutex::new(Server::default()));
    let (_backend, panel, cx) = opened(cx, server.clone()).await;
    panel.update(cx, |panel, cx| panel.check(cx));
    settled(&panel, cx, |panel| panel._command.is_none()).await;
    panel.update(cx, |panel, cx| {
        panel.method_id = Some("advertised-method".into());
        panel.start(cx)
    });
    settled(&panel, cx, |panel| {
        panel
            .projection
            .operation
            .as_ref()
            .is_some_and(|row| row.state == "waiting_for_person")
    })
    .await;
    let commands = server.lock().unwrap().commands.clone();
    assert_eq!(commands.len(), 2);
    assert_eq!(commands[0]["entityId"], "codex/");
    assert_eq!(commands[0]["expectedVersion"], 7);
    assert_eq!(commands[0]["machine"], "owner-engine");
    assert_eq!(commands[0]["command"], json!({"kind":"check_provider"}));
    assert_eq!(commands[1]["expectedVersion"], 0);
    assert_eq!(commands[1]["entityId"].as_str().unwrap().len(), 32);
    assert_eq!(
        commands[1]["command"],
        json!({"kind":"start_sign_in","provider":"codex","accountContext":"","methodId":"advertised-method","originNeedId":null})
    );
    panel.update(cx, |panel, cx| panel.cancel(cx));
    settled(&panel, cx, |panel| {
        panel
            .projection
            .operation
            .as_ref()
            .is_some_and(|row| row.state == "cancelled")
    })
    .await;
    let commands = &server.lock().unwrap().commands;
    assert_eq!(commands[2]["entityId"], commands[1]["entityId"]);
    assert_eq!(commands[2]["expectedVersion"], 1);
    assert_eq!(commands[2]["command"], json!({"kind":"cancel_sign_in"}));
}
#[gpui::test]
async fn already_running_joins_the_actual_paged_operation_instead_of_rejected_candidate(
    cx: &mut TestAppContext,
) {
    let server = Arc::new(Mutex::new(Server {
        already_running: true,
        ..Server::default()
    }));
    let (_backend, panel, cx) = opened(cx, server.clone()).await;
    panel.update(cx, |panel, cx| panel.start(cx));
    settled(&panel, cx, |panel| {
        panel.projection.operation_id.as_deref() == Some("actual-other-window")
            && panel.projection.ready
    })
    .await;
    let server = server.lock().unwrap();
    assert_eq!(server.commands.len(), 1);
    assert_ne!(server.commands[0]["entityId"], "actual-other-window");
    panel.read_with(cx, |panel, _| {
        assert_eq!(panel.projection.operation.as_ref().unwrap().version, 9)
    });
}
#[gpui::test]
async fn actual_click_handler_never_acks_failed_opens_or_an_obsolete_displayed_link(
    cx: &mut TestAppContext,
) {
    let server = Arc::new(Mutex::new(Server {
        operation: Some(operation("existing", 9, "waiting_for_person")),
        ..Server::default()
    }));
    let (_backend, panel, cx) = opened(cx, server.clone()).await;
    settled(&panel, cx, |panel| panel.challenge.current.is_some()).await;
    let captured = panel.read_with(cx, |panel, _| {
        let challenge = panel.challenge.current.as_ref().unwrap();
        super::challenge::DisplayedLink {
            machine: "owner-engine".into(),
            operation_id: challenge.operation_id.clone(),
            generation: challenge.generation,
            original_url: challenge.link.clone().unwrap(),
        }
    });
    panel.update(cx, |panel, cx| {
        panel.open_link_using(captured.clone(), |_, _| Task::ready(false), cx)
    });
    settled(&panel, cx, |panel| panel._opening.is_none()).await;
    assert!(server.lock().unwrap().acks.is_empty());
    let (answer, receiver) = oneshot::channel();
    panel.update(cx, |panel, cx| {
        panel.open_link_using(
            captured.clone(),
            |url, cx| {
                assert_eq!(url, captured.original_url);
                cx.foreground_executor()
                    .spawn(async move { receiver.await.unwrap() })
            },
            cx,
        )
    });
    panel.update(cx,|panel,_|{
        panel.challenge.install(serde_json::from_value(json!({"operationId":"existing","generation":2,"link":captured.original_url,"wantsCode":false})).unwrap());
    });
    answer.send(true).unwrap();
    cx.run_until_parked();
    assert!(server.lock().unwrap().acks.is_empty());
    panel.update(cx, |panel, cx| {
        let fresh = super::challenge::DisplayedLink {
            generation: 2,
            ..captured.clone()
        };
        panel.open_link_using(fresh, |_, _| Task::ready(true), cx);
    });
    settled(&panel, cx, |panel| panel._opening.is_none()).await;
    let server = server.lock().unwrap();
    assert_eq!(server.acks.len(), 1);
    assert_eq!(
        server.acks[0],
        json!({"operationId":"existing","expectedGeneration":2,"originalUrl":captured.original_url,"machine":"owner-engine"})
    );
}

async fn invalidate_provider(cx: &mut VisualTestContext) {
    let call = cx.read(|cx| {
        SocketClient::global(cx).read(cx).call::<Value>(
            "test",
            "invalidate_provider",
            json!({}),
            None,
        )
    });
    assert!(matches!(
        call.await,
        crate::core::socket::outcome::Outcome::Ok(_)
    ));
}
#[gpui::test]
async fn repeated_committed_invalidations_during_a_read_coalesce_and_do_not_get_lost(
    cx: &mut TestAppContext,
) {
    let server = Arc::new(Mutex::new(Server::default()));
    let (_backend, panel, cx) = opened(cx, server.clone()).await;
    let (release, delayed) = std::sync::mpsc::channel();
    server.lock().unwrap().hold_provider = Some(delayed);
    invalidate_provider(cx).await;
    settled(&panel, cx, |panel| panel._provider.is_some()).await;
    invalidate_provider(cx).await;
    invalidate_provider(cx).await;
    cx.run_until_parked();
    assert_eq!(server.lock().unwrap().provider_reads, 2);
    server.lock().unwrap().provider_version = 8;
    // The owned bulk call has its real shared socket ID, obtained from the fake's observed traffic.
    let held_id = server.lock().unwrap().provider_reads;
    assert_eq!(held_id, 2);
    // Find the already-sent second provider entity call without issuing another read.
    let mut entity_id = None;
    while let Ok((path, frame)) = _backend.on.try_recv() {
        if path == "/socket/bulk"
            && frame["feature"] == "engine"
            && frame["method"] == "entity"
            && frame["args"]["entityKind"] == "provider"
        {
            entity_id = Some(frame["id"].clone());
        }
    }
    release
        .send(vec![Reply::Frame(
            json!({"t":"result","id":entity_id.unwrap(),"result":provider()}),
        )])
        .unwrap();
    settled(&panel, cx, |panel| {
        panel._provider.is_none()
            && panel
                .projection
                .provider
                .as_ref()
                .is_some_and(|row| row.version == 8)
    })
    .await;
    assert_eq!(server.lock().unwrap().provider_reads, 3);
}
#[gpui::test]
async fn reply_loss_reconnect_keeps_one_command_identity_for_an_explicit_retry(
    cx: &mut TestAppContext,
) {
    let server = Arc::new(Mutex::new(Server::default()));
    let (_backend, panel, cx) = opened(cx, server.clone()).await;
    let old_generation = panel.read_with(cx, |panel, _| panel.generation);
    server.lock().unwrap().drop_command = true;
    panel.update(cx, |panel, cx| panel.check(cx));
    settled(&panel, cx, |panel| {
        panel.generation > old_generation
            && panel.projection.ready
            && panel.retry_command.is_some()
            && panel._command.is_none()
    })
    .await;
    panel.update(cx, |panel, cx| panel.retry(cx));
    settled(&panel, cx, |panel| {
        panel._command.is_none() && panel.retry_command.is_none()
    })
    .await;
    let server = server.lock().unwrap();
    assert_eq!(server.commands.len(), 2);
    assert_eq!(server.commands[0], server.commands[1]);
    assert!(server.acks.is_empty());
}

#[gpui::test]
async fn a_cancel_conflict_refreshes_current_version_and_requires_a_new_explicit_cancel(
    cx: &mut TestAppContext,
) {
    let server = Arc::new(Mutex::new(Server {
        operation: Some(operation("existing", 9, "waiting_for_person")),
        cancel_conflict: true,
        ..Server::default()
    }));
    let (_backend, panel, cx) = opened(cx, server.clone()).await;
    panel.update(cx, |panel, cx| panel.cancel(cx));
    settled(&panel, cx, |panel| {
        panel
            .projection
            .operation
            .as_ref()
            .is_some_and(|row| row.version == 10)
            && panel._command.is_none()
    })
    .await;
    assert_eq!(server.lock().unwrap().commands.len(), 1);
    panel.update(cx, |panel, cx| panel.cancel(cx));
    settled(&panel, cx, |panel| {
        panel
            .projection
            .operation
            .as_ref()
            .is_some_and(|row| row.state == "cancelled")
    })
    .await;
    let server = server.lock().unwrap();
    assert_eq!(server.commands[0]["expectedVersion"], 9);
    assert_eq!(server.commands[1]["expectedVersion"], 10);
    assert_ne!(
        server.commands[0]["commandId"],
        server.commands[1]["commandId"]
    );
}
#[gpui::test]
async fn closing_the_actual_panel_unsubscribes_and_never_sends_cancel_sign_in(
    cx: &mut TestAppContext,
) {
    let server = Arc::new(Mutex::new(Server {
        operation: Some(operation("existing", 9, "waiting_for_person")),
        ..Server::default()
    }));
    let (backend, panel, cx) = opened(cx, server.clone()).await;
    settled(&panel, cx, |panel| panel.challenge.current.is_some()).await;
    cx.update(|window, _| window.remove_window());
    drop(panel);
    cx.cx.update(|_| {});
    cx.run_until_parked();
    loop {
        if backend.next()["t"] == "unsub" {
            break;
        }
    }
    assert!(server.lock().unwrap().commands.is_empty());
    assert!(server.lock().unwrap().acks.is_empty());
}
