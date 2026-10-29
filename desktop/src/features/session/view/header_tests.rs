//! The header on a stand-in backend (`FakeBackend`) whose keyed `sessions` topic sends a session and one it started.

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::{AppContext as _, Entity};
use serde_json::{Value, json};

use super::{Header, project};
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;

fn session(id: &str, parent: Option<&str>) -> Value {
    json!({ "sessionId": id, "agent": "claude", "repoUrl": "/r", "title": format!("Session {id}"),
        "machine": "Workbox Test", "parentSessionId": parent, "acpSessionId": null, "updatedAt": 1 })
}

/// Answers the hello, and each keyed `sessions` sub with that session: `h1` was started by `s1`.
fn backend() -> FakeBackend {
    FakeBackend::start(|frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        if frame["t"] != "sub" {
            return Vec::new();
        }
        let key = frame["key"].as_str().unwrap_or_default();
        let parent = (key == "h1").then_some("s1");
        let state = json!({"t": "state", "sub": frame["id"], "items": [{"key": key, "item": session(key, parent)}]});
        vec![Reply::Frame(state)]
    })
}

async fn shown(header: &Entity<Header>, session: &str, cx: &mut TestAppContext) {
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _heard = cx.update(|cx| {
        header.update(cx, |header, cx| header.show(Some(session.into()), cx));
        cx.observe(header, move |_, _| _ = tell.unbounded_send(()))
    });
    header.read_with(cx, |header, _| {
        assert!(header.item.is_none(), "the last session's header is gone")
    });
    until("the header fills", &mut changes, &changed, || {
        header.read_with(cx, |header, _| {
            header.item.as_ref().map(|item| item.session_id.as_str()) == Some(session)
        })
    })
    .await;
}

// Pins: g0-session#20
#[gpui::test]
async fn a_started_session_links_the_session_that_started_it(cx: &mut TestAppContext) {
    let backend = backend();
    cx.executor().allow_parking();
    cx.update(|cx| {
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint, cx);
    });
    let header = cx.new(|_| Header::default());
    shown(&header, "h1", cx).await;
    header.read_with(cx, |header, _| {
        let item = header.item.as_ref().unwrap();
        assert_eq!(item.parent_session_id.as_deref(), Some("s1"));
    });
    shown(&header, "s1", cx).await;
    header.read_with(cx, |header, _| {
        let item = header.item.as_ref().unwrap();
        assert_eq!(
            (item.title().as_str(), item.parent_session_id.as_deref()),
            ("Session s1", None)
        );
    });
}

// Pins: g0-shape#12
#[test]
fn the_title_row_names_the_project_by_its_last_part() {
    assert_eq!(project("https://github.com/jcoble/Assembly"), "Assembly");
    assert_eq!(
        project("https://github.com/jcoble/Assembly.git"),
        "Assembly"
    );
    assert_eq!(project("/home/me/dev/work/Assembly/"), "Assembly");
    assert_eq!(project(r"C:\dev\Assembly"), "Assembly");
}
