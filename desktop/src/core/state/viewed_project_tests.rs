use std::sync::{Arc, Mutex};

use futures_channel::mpsc::unbounded;
use serde_json::json;

use super::{SelectedSession, Viewed, ViewedProject};
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use crate::core::state::sessions::Sessions;

fn viewed() -> Viewed {
    Viewed {
        project_id: "p1".into(),
        machine: "local".into(),
        folder: "/w/other".into(),
        title: "other".into(),
    }
}

#[test]
fn its_id_is_what_the_backend_reads() {
    assert_eq!(viewed().id(), "view:7031:2f772f6f74686572");
}

#[gpui::test]
fn another_sessions_selection_does_not_see_it_and_back_forgets_it(cx: &mut gpui::TestAppContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        let selected = SelectedSession::global(cx);
        selected.update(cx, |selected, cx| selected.select(Some("s1".into()), cx));
        ViewedProject::set(Some("s1".into()), Some(viewed()), cx);
        assert_eq!(ViewedProject::viewed(cx), Some(viewed()));
        assert_eq!(ViewedProject::subject(cx).unwrap().0, viewed().id());
        selected.update(cx, |selected, cx| selected.select(Some("s2".into()), cx));
        assert_eq!(ViewedProject::viewed(cx), None);
        assert_eq!(ViewedProject::subject(cx).unwrap().0, "s2");
        selected.update(cx, |selected, cx| selected.select(Some("s1".into()), cx));
        assert_eq!(ViewedProject::viewed(cx), Some(viewed()));
        ViewedProject::set(Some("s1".into()), None, cx);
        assert_eq!(ViewedProject::subject(cx).unwrap().0, "s1");
    });
}

fn session_item(session: &str, state: &str) -> serde_json::Value {
    json!({"sessionId":session,"agent":"fixture","repoUrl":"/owned","title":null,"machine":null,"parentSessionId":null,"acpSessionId":null,"updatedAt":1,"activeRunId":null,"state":state})
}

#[gpui::test]
async fn the_backend_says_who_works_in_the_viewed_folder_and_is_asked_only_when_that_can_change(
    cx: &mut gpui::TestAppContext,
) {
    // What the backend answers, the calls it had, and the sessions topic to push on.
    let answer = Arc::new(Mutex::new(None::<String>));
    let asked = Arc::new(Mutex::new(Vec::<String>::new()));
    let sub = Arc::new(Mutex::new(json!(null)));
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let backend = {
        let (answer, asked, sub) = (answer.clone(), asked.clone(), sub.clone());
        FakeBackend::start(move |frame| {
            if let Some(replies) = hello(frame) {
                return replies;
            }
            let id = frame["id"].clone();
            let replies = match (frame["t"].as_str(), frame["method"].as_str()) {
                (Some("sub"), _) if frame["topic"] == "sessions" => {
                    *sub.lock().unwrap() = id.clone();
                    vec![Reply::Frame(json!({"t":"state","sub":id,"items":[]}))]
                }
                (Some("call"), Some("inUse")) => {
                    asked
                        .lock()
                        .unwrap()
                        .push(frame["args"]["session"].as_str().unwrap().to_string());
                    let session = answer.lock().unwrap().clone();
                    vec![Reply::Frame(
                        json!({"t":"result","id":id,"result":{"session":session}}),
                    )]
                }
                (Some("call"), Some("test_state")) => vec![
                    Reply::Frame(json!({
                        "t":"change","sub":*sub.lock().unwrap(),"key":frame["args"]["session"],
                        "item":session_item(frame["args"]["session"].as_str().unwrap(), frame["args"]["state"].as_str().unwrap()),
                    })),
                    Reply::Frame(json!({"t":"result","id":id,"result":{}})),
                ],
                _ => Vec::new(),
            };
            _ = tell.unbounded_send(());
            replies
        })
    };
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
        Sessions::start(cx);
        let selected = SelectedSession::global(cx);
        selected.update(cx, |selected, cx| selected.select(Some("s1".into()), cx));
    });
    until("the sessions list", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| Sessions::try_global(cx).is_some()) && *sub.lock().unwrap() != json!(null)
    })
    .await;

    // No view, no asking: an idle window sends nothing.
    assert!(asked.lock().unwrap().is_empty());
    cx.update(|cx| ViewedProject::set(Some("s1".into()), Some(viewed()), cx));
    until("asked on opening", &mut changes, &changed, || {
        cx.run_until_parked();
        asked.lock().unwrap().len() == 1
    })
    .await;
    assert_eq!(asked.lock().unwrap()[0], viewed().id());
    cx.run_until_parked();
    assert_eq!(cx.update(|cx| ViewedProject::read_only_reason(cx)), None);

    // A session starts: the backend, not the panel, decides it's this folder.
    *answer.lock().unwrap() = Some("Busy".into());
    let state = |session: &str, state: &str, cx: &mut gpui::TestAppContext| {
        cx.update(|cx| {
            SocketClient::global(cx).read(cx).call::<serde_json::Value>(
                "test",
                "test_state",
                json!({"session":session,"state":state}),
                None,
            )
        })
    };
    let _ = state("elsewhere", "running", cx).await;
    until("read-only named", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| ViewedProject::read_only_reason(cx))
            .is_some()
    })
    .await;
    assert_eq!(
        cx.update(|cx| ViewedProject::read_only_reason(cx)).unwrap(),
        "\"Busy\" is working in this folder, so it is read-only here."
    );
    assert_eq!(asked.lock().unwrap().len(), 2);

    // A change that can't change the answer asks nothing.
    let _ = state("idle-one", "idle", cx).await;
    cx.run_until_parked();
    assert_eq!(asked.lock().unwrap().len(), 2);

    // It finishes: writable again.
    *answer.lock().unwrap() = None;
    let _ = state("elsewhere", "idle", cx).await;
    until("writable again", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| ViewedProject::read_only_reason(cx))
            .is_none()
    })
    .await;

    // Back to the session: no more asking, whatever the sessions do.
    cx.update(|cx| ViewedProject::set(Some("s1".into()), None, cx));
    let count = asked.lock().unwrap().len();
    let _ = state("elsewhere", "running", cx).await;
    cx.run_until_parked();
    assert_eq!(asked.lock().unwrap().len(), count);
}
