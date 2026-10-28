//! Clicking fast across sessions on the socket client (`FakeBackend`): each superseded load is cancelled on the
//! backend, its rows subscription closed, and a page the backend answers anyway never lands.

use std::rc::Rc;
use std::sync::{Arc, Mutex};

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::{Entity, VisualTestContext};
use serde_json::{Value, json};

use super::SessionPane;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use crate::features::session::client::socket_session_client::SocketSessionClient;
use crate::features::session::model::item::ItemKind;

/// What the backend saw: each session's page call id, each cancel, each rows sub and unsub.
#[derive(Default)]
struct Seen {
    pages: Vec<(String, Value)>,
    cancels: Vec<Value>,
    subs: Vec<(String, Value)>,
    unsubs: Vec<Value>,
}

fn page(session: &str) -> Value {
    let row = json!({ "sessionId": session, "seq": 1, "runId": "r1", "itemId": null, "kind": "user",
        "payload": { "text": format!("asked in {session}") }, "createdAt": 1 });
    json!({ "rows": [row], "hasBefore": false, "activeRunId": null })
}

/// Each rows sub starts at seq 1, so the pane asks for the page below it. Only `last`'s page is answered at once;
/// the others wait, and answer when cancelled, as a load that finished just as the cancel came would.
fn backend(
    last: &'static str,
    seen: Arc<Mutex<Seen>>,
    heard: impl Fn() + Send + 'static,
) -> FakeBackend {
    FakeBackend::start(move |frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        let mut seen = seen.lock().unwrap();
        let id = frame["id"].clone();
        let replies = match (
            frame["t"].as_str(),
            frame["topic"].as_str(),
            frame["method"].as_str(),
        ) {
            (Some("sub"), Some("rows"), _) => {
                let session = frame["key"].as_str().unwrap_or_default().to_string();
                seen.subs.push((session, id.clone()));
                vec![Reply::Frame(
                    json!({"t": "state", "sub": id, "seq": 1, "items": []}),
                )]
            }
            // The header's `sessions` subs come and go too; only the rows ones count here.
            (Some("unsub"), ..) => {
                if seen.subs.iter().any(|(_, sub)| *sub == id) {
                    seen.unsubs.push(id);
                }
                Vec::new()
            }
            (Some("call"), _, Some("page")) => {
                let session = frame["args"]["session"]
                    .as_str()
                    .unwrap_or_default()
                    .to_string();
                let answer = (session == last).then(|| {
                    Reply::Frame(json!({"t": "result", "id": id, "result": page(&session)}))
                });
                seen.pages.push((session, id));
                answer.into_iter().collect()
            }
            // The composer's config reads are cancelled as sessions change too; only the pages count here.
            (Some("cancel"), ..) => {
                let session = seen
                    .pages
                    .iter()
                    .find(|(_, page)| *page == id)
                    .map(|(session, _)| session.clone());
                if session.is_some() {
                    seen.cancels.push(id.clone());
                }
                session
                    .map(|session| {
                        Reply::Frame(json!({"t": "result", "id": id, "result": page(&session)}))
                    })
                    .into_iter()
                    .collect()
            }
            _ => Vec::new(),
        };
        drop(seen);
        heard();
        replies
    })
}

/// The user prompts the pane shows.
fn prompts(pane: &Entity<SessionPane>, cx: &mut VisualTestContext) -> Vec<String> {
    cx.update(|_, cx| {
        let items = &pane.read(cx).view.items;
        items
            .iter()
            .filter_map(|item| match &item.kind {
                ItemKind::User { text, .. } => Some(text.clone()),
                _ => None,
            })
            .collect()
    })
}

// Pins: g0-session#1
#[gpui::test]
async fn clicking_across_sessions_cancels_each_superseded_load_and_no_stale_page_lands(
    cx: &mut TestAppContext,
) {
    let sessions = ["s1", "s2", "s3", "s4", "s5"];
    let seen = Arc::new(Mutex::new(Seen::default()));
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let backend = backend("s5", seen.clone(), move || _ = tell.unbounded_send(()));
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
    // Each click comes once the last session's page is on its way, before it's answered.
    for (at, session) in sessions.iter().enumerate() {
        pane.update_in(cx, |pane, window, cx| {
            pane.open(session.to_string(), window, cx)
        });
        until("the page is asked for", &mut changes, &changed, || {
            seen.lock().unwrap().pages.len() > at
        })
        .await;
    }
    until("the last page lands", &mut changes, &changed, || {
        prompts(&pane, cx) == ["asked in s5"] && seen.lock().unwrap().cancels.len() == 4
    })
    .await;
    // The stale answers to the cancels have come and gone by the time s5's own rows are drawn again.
    cx.run_until_parked();
    assert_eq!(
        prompts(&pane, cx),
        ["asked in s5"],
        "no other session's page landed"
    );
    let seen = seen.lock().unwrap();
    let pages: Vec<&str> = seen
        .pages
        .iter()
        .map(|(session, _)| session.as_str())
        .collect();
    assert_eq!(pages, sessions, "one page call per click");
    let superseded: Vec<&Value> = seen.pages[..4].iter().map(|(_, id)| id).collect();
    assert_eq!(
        seen.cancels.iter().collect::<Vec<_>>(),
        superseded,
        "each superseded load is cancelled"
    );
    let closed: Vec<&Value> = seen.subs[..4].iter().map(|(_, id)| id).collect();
    assert_eq!(
        seen.unsubs.iter().collect::<Vec<_>>(),
        closed,
        "each left session's rows stop"
    );
}

// Pins: g0-session#1
#[gpui::test]
async fn clicks_faster_than_the_rows_arrive_load_only_the_last_session(cx: &mut TestAppContext) {
    let seen = Arc::new(Mutex::new(Seen::default()));
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let backend = backend("s5", seen.clone(), move || _ = tell.unbounded_send(()));
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
    pane.update_in(cx, |pane, window, cx| {
        for session in ["s1", "s2", "s3", "s4", "s5"] {
            pane.open(session.into(), window, cx);
        }
    });
    until("the last page lands", &mut changes, &changed, || {
        prompts(&pane, cx) == ["asked in s5"]
    })
    .await;
    let seen = seen.lock().unwrap();
    let pages: Vec<&str> = seen
        .pages
        .iter()
        .map(|(session, _)| session.as_str())
        .collect();
    assert_eq!(
        pages,
        ["s5"],
        "a session left before its rows came never loads a page"
    );
    assert_eq!(
        seen.unsubs.len(),
        seen.subs.len() - 1,
        "only the last session's rows stay open"
    );
}
