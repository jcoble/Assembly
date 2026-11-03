//! Canonical window-command fixtures; no provider/native execution.
use super::commands::{NeedAnswer, resolve_command};
use super::{EngineCursor, EngineKey, EngineScope, EngineStore};
use serde_json::json;

fn store() -> (EngineStore, EngineKey) {
    let mut store = EngineStore::default();
    let scope = EngineScope {
        machine: "real-owner".into(),
        topic: "mirror".into(),
        key: None,
    };
    store.snapshot(scope,EngineCursor {through:4,at:None},vec![
        json!({"entityKind":"needs_me","machine":"real-owner","id":"n","version":3,
            "sessionId":"s","kind":"permission","state":"open","text":"Actual reason"}),
        json!({"entityKind":"session","machine":"real-owner","id":"s","version":7,"epoch":9})
    ]).unwrap();
    (
        store,
        EngineKey {
            machine: "real-owner".into(),
            kind: "needs_me".into(),
            id: "n".into(),
        },
    )
}

#[test]
fn resolving_uses_actual_source_need_version_session_epoch_and_exact_answer() {
    let (store, key) = store();
    let answer = json!({"optionId":"actual-provider-option"});
    let command = resolve_command(
        &store,
        &key,
        NeedAnswer::Answer(answer.clone()),
        Some("rail"),
    )
    .unwrap();
    uuid::Uuid::parse_str(&command.command_id).unwrap();
    assert_eq!(command.expected_version, 3);
    assert_eq!(command.epoch, 9);
    assert_eq!(command.machine.as_deref(), Some("real-owner"));
    assert_eq!(command.surface.as_deref(), Some("rail"));
    assert_eq!(
        command.command,
        json!({"kind":"resolve_need","state":"answered","answer":answer})
    );
    let wire = serde_json::to_value(command).unwrap();
    assert!(wire.get("actor").is_none());
    assert!(wire.get("origin").is_none());
    assert!(wire.get("deviceId").is_none());
}

#[test]
fn missing_same_source_session_epoch_refuses_instead_of_defaulting_or_using_an_alias() {
    let (mut store, key) = store();
    let scope = EngineScope {
        machine: "real-owner".into(),
        topic: "mirror".into(),
        key: None,
    };
    store
        .snapshot(
            scope,
            EngineCursor {
                through: 5,
                at: None,
            },
            vec![
                json!({"entityKind":"needs_me","machine":"real-owner","id":"n","version":3,
            "sessionId":"s","kind":"question","state":"open"}),
            ],
        )
        .unwrap();
    let other = EngineScope {
        machine: "other-owner".into(),
        topic: "sessions".into(),
        key: None,
    };
    store.snapshot(other,EngineCursor {through:5,at:None},vec![
        json!({"entityKind":"session","machine":"other-owner","id":"s","version":7,"epoch":99})
    ]).unwrap();
    assert_eq!(
        resolve_command(&store, &key, NeedAnswer::Dismiss, None)
            .unwrap_err()
            .code,
        "session_unavailable"
    );
}

#[test]
fn dismiss_is_the_existing_resolve_transition_without_an_invented_option() {
    let (store, key) = store();
    let command = resolve_command(&store, &key, NeedAnswer::Dismiss, None).unwrap();
    assert_eq!(
        command.command,
        json!({"kind":"resolve_need","state":"dismissed","answer":null})
    );
}

#[test]
fn surface_is_an_explicit_allowed_hint_and_never_inferred_as_identity() {
    let (store, key) = store();
    assert_eq!(
        resolve_command(&store, &key, NeedAnswer::Dismiss, Some("board"))
            .unwrap()
            .surface
            .as_deref(),
        Some("board")
    );
    assert!(
        resolve_command(&store, &key, NeedAnswer::Dismiss, None)
            .unwrap()
            .surface
            .is_none()
    );
    assert_eq!(
        resolve_command(&store, &key, NeedAnswer::Dismiss, Some("phone"))
            .unwrap_err()
            .code,
        "bad_args"
    );
}

#[gpui::test]
async fn a_dispatched_command_with_a_lost_reply_is_unknown_and_is_not_sent_again(
    cx: &mut gpui::TestAppContext,
) {
    use super::commands::WindowCommand;
    use crate::core::socket::{
        client::SocketClient,
        connection_state::ConnectionState,
        endpoint::Endpoint,
        fake_backend::{FakeBackend, Reply},
        outcome::Outcome,
        test_wait::until,
    };
    use futures_channel::mpsc::unbounded;
    use gpui_kit::*;
    let mut commands = 0;
    let backend = FakeBackend::start(move |frame| match frame["t"].as_str() {
        Some("hello") => vec![Reply::Frame(
            json!({"t":"welcome","protocol":1,"machine":"friendly","engine":"real-owner"}),
        )],
        Some("call") if frame["method"] == "command" => {
            commands += 1;
            vec![Reply::Drop]
        }
        Some("call") => vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":{"commands":commands}}),
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
    let feed = cx.update(super::EngineFeed::global);
    let command = WindowCommand {
        command_id: uuid::Uuid::new_v4().to_string(),
        entity_id: "n".into(),
        expected_version: 3,
        epoch: 9,
        machine: Some("real-owner".into()),
        surface: Some("rail".into()),
        command: json!({"kind":"resolve_need","state":"answered","answer":"actual answer"}),
    };
    let submitted = cx.update(|cx| feed.update(cx, |feed, cx| feed.submit(command, cx)));
    assert_eq!(submitted.await.unwrap_err().code, "outcome_unknown");
    assert_eq!(backend.next()["method"], "command");
    assert_eq!(backend.next()["t"], "hello");
    let barrier = cx.update(|cx| {
        socket
            .read(cx)
            .call::<serde_json::Value>("test", "barrier", json!({}), None)
    });
    match barrier.await {
        Outcome::Ok(reply) => assert_eq!(reply["commands"], 1),
        other => panic!("barrier failed: {other:?}"),
    }
}
