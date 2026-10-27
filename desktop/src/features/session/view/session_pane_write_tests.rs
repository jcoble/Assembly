//! A write a row's button started keeps going when the reader switches sessions at once (`FakeBackend`): only loads
//! drop with the session left behind.

use std::rc::Rc;
use std::sync::{Arc, Mutex};

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use serde_json::{Value, json};

use super::SessionPane;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use crate::features::session::client::socket_session_client::SocketSessionClient;
use crate::features::session::model::item::ItemKind;
use crate::features::session::view::session_action::SessionAction;

/// What the backend saw: the answer calls' ids and args, and every cancel.
#[derive(Default)]
struct Seen {
    answers: Vec<(Value, Value)>,
    cancels: Vec<Value>,
}

/// s1 holds an open ask; s2 a prompt. Answers are held, as a machine still busy with one would.
fn page(session: &str) -> Value {
    let rows = if session == "s1" {
        json!([{ "sessionId": "s1", "seq": 1, "runId": "r1", "itemId": "ask:a1", "kind": "ask",
            "payload": { "kind": "permission", "state": "open", "options": [], "answer": null }, "createdAt": 1 }])
    } else {
        json!([{ "sessionId": session, "seq": 1, "runId": "r1", "itemId": null, "kind": "user",
            "payload": { "text": format!("asked in {session}") }, "createdAt": 1 }])
    };
    json!({ "rows": rows, "hasBefore": false, "activeRunId": null })
}

fn backend(seen: Arc<Mutex<Seen>>, heard: impl Fn() + Send + 'static) -> FakeBackend {
    FakeBackend::start(move |frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        let id = frame["id"].clone();
        let replies = match (
            frame["t"].as_str(),
            frame["topic"].as_str(),
            frame["method"].as_str(),
        ) {
            (Some("sub"), Some("rows"), _) => vec![Reply::Frame(
                json!({"t": "state", "sub": id, "seq": 1, "items": []}),
            )],
            (Some("call"), _, Some("page")) => {
                let session = frame["args"]["session"].as_str().unwrap_or_default();
                vec![Reply::Frame(
                    json!({"t": "result", "id": id, "result": page(session)}),
                )]
            }
            (Some("call"), _, Some("answer")) => {
                seen.lock()
                    .unwrap()
                    .answers
                    .push((id, frame["args"].clone()));
                Vec::new()
            }
            (Some("cancel"), ..) => {
                seen.lock().unwrap().cancels.push(id);
                Vec::new()
            }
            _ => Vec::new(),
        };
        heard();
        replies
    })
}

// Pins: g0-session#16
#[gpui::test]
async fn an_answer_still_reaches_the_backend_when_the_reader_switches_at_once(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Seen::default()));
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let backend = backend(seen.clone(), move || _ = tell.unbounded_send(()));
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint, cx);
    });
    let (pane, cx) =
        cx.add_window_view(|window, cx| SessionPane::new(Rc::new(SocketSessionClient), window, cx));
    let tell = changed.clone();
    let _heard = cx.update(|_, cx| cx.observe(&pane, move |_, _| _ = tell.unbounded_send(())));
    pane.update_in(cx, |pane, window, cx| pane.open("s1".into(), window, cx));
    until("s1's ask shows", &mut changes, &changed, || {
        cx.update(|_, cx| {
            let items = &pane.read(cx).view.items;
            items
                .iter()
                .any(|item| matches!(item.kind, ItemKind::Ask { .. }))
        })
    })
    .await;
    let answer = json!({ "optionId": "allow" });
    pane.update_in(cx, |pane, window, cx| {
        let action = SessionAction::Answer {
            ask_id: "a1".into(),
            answer: answer.clone(),
        };
        pane.on_action(action, window, cx);
        pane.open("s2".into(), window, cx);
    });
    until("s2 shows", &mut changes, &changed, || {
        let shown = cx.update(|_, cx| {
            let items = &pane.read(cx).view.items;
            items.iter().any(
                |item| matches!(&item.kind, ItemKind::User { text, .. } if text == "asked in s2"),
            )
        });
        shown && !seen.lock().unwrap().answers.is_empty()
    })
    .await;
    cx.run_until_parked();
    let seen = seen.lock().unwrap();
    assert_eq!(
        seen.answers
            .iter()
            .map(|(_, args)| args)
            .collect::<Vec<_>>(),
        [&json!({ "session": "s1", "ask": "a1", "answer": answer })]
    );
    let (id, _) = &seen.answers[0];
    assert!(
        !seen.cancels.contains(id),
        "the switch didn't cancel the answer"
    );
}
