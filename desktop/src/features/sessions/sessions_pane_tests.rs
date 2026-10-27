//! The sessions column follows the backend: a column opened before the backend's first ready line takes the sessions
//! list once it starts, it redraws when the connection drops, and what an empty column says follows the connection.

use std::cell::Cell;
use std::rc::Rc;

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;

use super::{SessionsPane, empty_line};
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::real_backend::RealBackend;
use crate::core::socket::test_wait::until;
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
async fn a_column_opened_before_the_backend_was_ready_follows_it(cx: &mut TestAppContext) {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    cx.executor().allow_parking();
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    let (pane, cx) = cx.add_window_view(|_, cx| SessionsPane::new(cx));
    pane.read_with(cx, |pane, _| assert!(pane.sessions.is_none()));
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let (socket, _heard) = cx.update(|_, cx| {
        let socket = SocketClient::start(backend.endpoint(), cx);
        Sessions::start(cx);
        let heard = cx.observe(&socket, move |_, _| _ = tell.unbounded_send(()));
        (socket, heard)
    });
    pane.read_with(cx, |pane, _| assert!(pane.sessions.is_some()));
    let state =
        |cx: &mut gpui_kit::VisualTestContext| cx.update(|_, cx| socket.read(cx).state().clone());
    until("connected", &mut changes, &changed, || {
        matches!(state(cx), ConnectionState::Connected { .. })
    })
    .await;
    // Only the pane's own observer tells it the connection dropped.
    let (redrawn, mut redraws) = unbounded();
    let (drawn, tell) = (Rc::new(Cell::new(false)), redrawn.clone());
    let marked = drawn.clone();
    let _drawn = cx.update(|_, cx| {
        cx.observe(&pane, move |_, _| {
            marked.set(true);
            _ = tell.unbounded_send(());
        })
    });
    drop(backend);
    until(
        "the pane redraws for the drop",
        &mut redraws,
        &redrawn,
        || drawn.get() && !matches!(state(cx), ConnectionState::Connected { .. }),
    )
    .await;
    assert_ne!(empty_line(Some(&state(cx))), "No sessions yet.");
}
