//! The sessions column follows the backend: a column opened before the backend's first ready line takes the sessions
//! list once it starts, and what an empty column says follows the connection.

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;

use super::{SessionsPane, empty_line};
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::real_backend::RealBackend;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::Sessions;

// Pins: g0-session#30
#[test]
fn an_empty_column_says_what_the_connection_is_doing() {
    let connected = ConnectionState::Connected {
        machine: "m".into(),
    };
    assert_eq!(empty_line(None), "The backend isn't running.");
    assert_eq!(
        empty_line(Some(&ConnectionState::Connecting)),
        "Connecting to the backend…"
    );
    assert_eq!(empty_line(Some(&connected)), "No sessions yet.");
    assert_eq!(
        empty_line(Some(&ConnectionState::Disconnected)),
        "The backend isn't running."
    );
    assert_eq!(
        empty_line(Some(&ConnectionState::Refused { code: 4401 })),
        "The backend isn't running."
    );
}

// Pins: g0-session#30
#[gpui::test]
async fn a_column_opened_before_the_backend_was_ready_takes_its_sessions(cx: &mut TestAppContext) {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    cx.executor().allow_parking();
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    let (pane, cx) = cx.add_window_view(|_, cx| SessionsPane::new(cx));
    pane.read_with(cx, |pane, _| assert!(pane.sessions.is_none()));
    let (states, mut changes) = unbounded();
    let _heard = cx.update(|_, cx| {
        let socket = SocketClient::start(backend.endpoint(), cx);
        Sessions::start(cx);
        cx.subscribe(&socket, move |_, state: &ConnectionState, _| {
            _ = states.unbounded_send(state.clone())
        })
    });
    pane.read_with(cx, |pane, _| assert!(pane.sessions.is_some()));
    while let Ok(state) = changes.recv().await {
        if matches!(state, ConnectionState::Connected { .. }) {
            break;
        }
    }
    let state = cx.update(|_, cx| SocketClient::global(cx).read(cx).state().clone());
    assert_eq!(empty_line(Some(&state)), "No sessions yet.");
}
