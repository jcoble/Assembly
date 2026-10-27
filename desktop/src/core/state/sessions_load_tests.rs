//! An older page still loading when a reconnect's state leaves a gap is stale: the gap cancels it on the backend
//! (`FakeBackend`), and the page the backend answers anyway never lands in the new list.

use std::sync::{Arc, Mutex};

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
use serde_json::{Value, json};

use super::Sessions;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;

/// The `sessions` sub's id, the page call's id, and whether that call was cancelled.
#[derive(Default)]
struct Seen {
    sub: Value,
    page: Option<Value>,
    cancelled: bool,
}

/// The topic's 200, `{prefix}0` to `{prefix}199`, updated at `from` onwards.
fn state(sub: &Value, prefix: &str, from: i64) -> Reply {
    let items: Vec<Value> = (0..200)
        .map(|n| {
            let id = format!("{prefix}{n}");
            json!({"key": id, "item": {"sessionId": id, "agent": "claude", "repoUrl": "/r", "title": null,
                "machine": null, "parentSessionId": null, "acpSessionId": null, "updatedAt": from + n,
                "summary": {}}})
        })
        .collect();
    Reply::Frame(json!({"t": "state", "sub": sub, "items": items}))
}

/// The first state is a0-a199. The page call is answered with no page but a reconnect's state, x0-x199, all newer:
/// a gap. The page itself, b at 500, is answered only once the call is cancelled.
fn backend(seen: Arc<Mutex<Seen>>, heard: impl Fn() + Send + 'static) -> FakeBackend {
    FakeBackend::start(move |frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        let mut seen = seen.lock().unwrap();
        let id = frame["id"].clone();
        let replies = match (frame["t"].as_str(), frame["method"].as_str()) {
            (Some("sub"), _) => {
                seen.sub = id.clone();
                vec![state(&id, "a", 1000)]
            }
            (Some("call"), Some("page")) => {
                seen.page = Some(id);
                vec![state(&seen.sub, "x", 5000)]
            }
            (Some("cancel"), _) if seen.page.as_ref() == Some(&id) => {
                seen.cancelled = true;
                let page = json!({"sessions": [{"sessionId": "b", "agent": "claude", "repoUrl": "/r",
                    "title": null, "machine": null, "parentSessionId": null, "acpSessionId": null,
                    "updatedAt": 500, "summary": {}}], "nextBefore": 500, "nextBeforeId": "b"});
                vec![Reply::Frame(
                    json!({"t": "result", "id": id, "result": page}),
                )]
            }
            _ => Vec::new(),
        };
        drop(seen);
        heard();
        replies
    })
}

#[gpui::test]
async fn a_gap_cancels_the_older_page_still_loading_and_its_page_never_lands(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Seen::default()));
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let backend = backend(seen.clone(), move || _ = tell.unbounded_send(()));
    cx.executor().allow_parking();
    let sessions = cx.update(|cx| {
        gpui_kit::init(cx);
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint, cx);
        cx.new(Sessions::new)
    });
    let tell = changed.clone();
    let _heard = cx.update(|cx| cx.observe(&sessions, move |_, _| _ = tell.unbounded_send(())));
    until("the first state", &mut changes, &changed, || {
        sessions.read_with(cx, |sessions, _| sessions.older.is_some())
    })
    .await;
    sessions.update(cx, |sessions, cx| sessions.load_older(cx));
    until(
        "the stale load is cancelled",
        &mut changes,
        &changed,
        || seen.lock().unwrap().cancelled,
    )
    .await;
    // The page answered to the cancel has come and gone.
    cx.run_until_parked();
    sessions.read_with(cx, |sessions, _| {
        assert!(
            sessions
                .items
                .iter()
                .all(|item| item.session_id.starts_with('x'))
        );
        assert_eq!(sessions.items.len(), 200);
        assert_eq!(sessions.older, Some((5000, "x0".to_owned())));
        assert!(sessions.loading_older.is_none());
    });
}
