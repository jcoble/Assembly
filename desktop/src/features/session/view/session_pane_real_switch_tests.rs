//! Clicking fast across sessions on the real backend (`RealBackend`, with a stub agent): only the last session's
//! messages show, whether the clicks beat the rows or each comes once the last page was asked for.

use std::rc::Rc;

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::{Entity, VisualTestContext};
use serde_json::{Value, json};

use super::SessionPane;
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::real_backend_cancel_tests::backend_with_stub;
use crate::core::socket::test_wait::until;
use crate::features::session::client::socket_session_client::SocketSessionClient;
use crate::features::session::model::item::ItemKind;

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

async fn call(
    socket: &Entity<SocketClient>,
    method: (&str, &str),
    args: Value,
    cx: &mut TestAppContext,
) -> Value {
    let pending = cx.update(|cx| {
        socket
            .read(cx)
            .call::<Value>(method.0, method.1, args, None)
    });
    match pending.await {
        Outcome::Ok(value) => value,
        other => panic!("{}/{}: {other:?}", method.0, method.1),
    }
}

// Pins: g0-session#1
#[gpui::test]
async fn clicking_across_real_sessions_shows_only_the_last_ones_messages(cx: &mut TestAppContext) {
    let Some((backend, repo)) = backend_with_stub() else {
        return;
    };
    cx.executor().allow_parking();
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let (socket, _heard) = cx.update(|cx| {
        gpui_kit::init(cx);
        let socket = SocketClient::start(backend.endpoint(), cx);
        let heard = cx.observe(&socket, move |_, _| _ = tell.unbounded_send(()));
        (socket, heard)
    });
    until("connected", &mut changes, &changed, || {
        cx.update(|cx| matches!(socket.read(cx).state(), ConnectionState::Connected { .. }))
    })
    .await;
    let mut sessions = Vec::new();
    for at in 1..=5 {
        let created = call(
            &socket,
            ("sessions", "create"),
            json!({ "agent": "stub", "repoUrl": repo }),
            cx,
        )
        .await;
        let session = created["sessionId"]
            .as_str()
            .expect("a session id")
            .to_string();
        let text = format!("asked in {at}");
        call(
            &socket,
            ("send", "send"),
            json!({ "session": session, "text": text }),
            cx,
        )
        .await;
        sessions.push(session);
    }

    let (pane, cx) =
        cx.add_window_view(|window, cx| SessionPane::new(Rc::new(SocketSessionClient), window, cx));
    let tell = changed.clone();
    let _pane_heard = cx.update(|_, cx| cx.observe(&pane, move |_, _| _ = tell.unbounded_send(())));
    // Faster than the rows: five clicks in one go.
    pane.update_in(cx, |pane, window, cx| {
        for session in &sessions {
            pane.open(session.clone(), window, cx);
        }
    });
    until("the last session shows", &mut changes, &changed, || {
        prompts(&pane, cx) == ["asked in 5"]
    })
    .await;
    // Each click once the session before has asked for its page.
    for session in sessions.iter().rev() {
        pane.update_in(cx, |pane, window, cx| {
            pane.open(session.clone(), window, cx)
        });
        until("its page is asked for", &mut changes, &changed, || {
            cx.update(|_, cx| pane.read(cx)._page.is_some())
        })
        .await;
    }
    until("the first session shows", &mut changes, &changed, || {
        prompts(&pane, cx) == ["asked in 1"]
    })
    .await;
    cx.run_until_parked();
    assert_eq!(
        prompts(&pane, cx),
        ["asked in 1"],
        "no other session's page landed"
    );
}
