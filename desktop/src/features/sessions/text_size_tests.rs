//! Text-size changes remeasure real rail rows without changing the selected session.
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use serde_json::json;

use super::rail_page::{RailPage, RailTotals, test_entry};
use super::sessions_pane::SessionsPane;
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::outcome::Outcome;
use crate::core::socket::test_wait::until;
use crate::core::state::appearance::UI_FONT_SIZE;
use crate::core::state::preferences::Preferences;
use crate::core::state::selected_session::SelectedSession;

#[gpui::test]
async fn a_size_change_remeasures_the_list(cx: &mut TestAppContext) {
    let backend = FakeBackend::start(|frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        if frame["method"] == "set" {
            return vec![Reply::Frame(
                json!({"t":"result", "id":frame["id"], "result":{}}),
            )];
        }
        Vec::new()
    });
    cx.executor().allow_parking();
    let socket = cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        Preferences::init(cx);
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
    until("socket ready", &mut changes, &changed, || {
        cx.update(|cx| matches!(socket.read(cx).state(), ConnectionState::Connected { .. }))
    })
    .await;
    let (pane, cx) = cx.add_window_view(SessionsPane::new);
    pane.update(cx, |pane, cx| {
        let (id, _) = pane.rail.changed().unwrap();
        pane.answer(
            id,
            Outcome::Ok(RailPage {
                totals: RailTotals::default(),
                agents: vec![],
                sessions: vec![test_entry("sizing", "/Assembly")],
                next: None,
                start: 0,
            }),
            cx,
        );
    });
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let before = cx.debug_bounds("session-sizing").unwrap().size.height;
    cx.update(|_, cx| Preferences::save(UI_FONT_SIZE.key, json!(18), cx));
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let after = cx.debug_bounds("session-sizing").unwrap().size.height;
    assert!(after > before, "larger text must remeasure the held row");
    pane.read_with(cx, |pane, _| {
        assert_eq!(pane.rail.sessions[0].session_id, "sizing")
    });
}
