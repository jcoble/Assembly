//! A rail pick hands the keys to the picked session's composer: the click that picked it focused the rail, and keys
//! typed from then on, while the open is still on the way, land in the picked session's empty draft.

use std::rc::Rc;

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::{Entity, Focusable as _, VisualTestContext};
use serde_json::json;

use super::SessionPane;
use crate::core::intents::session_open::SessionOpenTarget;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply};
use crate::core::socket::test_wait::until;
use crate::core::state::selected_session::SelectedSession;
use crate::features::session::client::socket_session_client::SocketSessionClient;

/// A pane on a backend that never answers `resolve_source`, so every pick stays on its way; the window's focus starts
/// on the pane itself, as it does on the rail after a click there.
async fn pane(
    cx: &mut TestAppContext,
) -> (FakeBackend, Entity<SessionPane>, &mut VisualTestContext) {
    let backend = FakeBackend::start(|frame| match frame["t"].as_str() {
        Some("hello") => vec![Reply::Frame(
            json!({"t": "welcome", "protocol": 1, "machine": "box", "engine": "local"}),
        )],
        _ => Vec::new(),
    });
    cx.executor().allow_parking();
    let (main, bulk) = cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        (
            SocketClient::start(endpoint.clone(), cx),
            SocketClient::start_bulk(endpoint, cx),
        )
    });
    let (changed, mut changes) = unbounded();
    let (tell, tell_bulk) = (changed.clone(), changed.clone());
    let _main = cx.update(|cx| cx.observe(&main, move |_, _| _ = tell.unbounded_send(())));
    let _bulk = cx.update(|cx| cx.observe(&bulk, move |_, _| _ = tell_bulk.unbounded_send(())));
    until("both sockets", &mut changes, &changed, || {
        cx.update(|cx| {
            main.read(cx).connection_generation().is_some()
                && bulk.read(cx).connection_generation().is_some()
        })
    })
    .await;
    let (pane, cx) =
        cx.add_window_view(|window, cx| SessionPane::new(Rc::new(SocketSessionClient), window, cx));
    pane.update_in(cx, |pane, window, cx| {
        window.focus(&pane.focus_handle(cx), cx)
    });
    cx.run_until_parked();
    (backend, pane, cx)
}

fn pick(pane: &Entity<SessionPane>, target: SessionOpenTarget, cx: &mut VisualTestContext) {
    pane.update_in(cx, |pane, window, cx| {
        pane.request_current_route(None, "s2".into(), "pick-1".into(), target, window, cx)
    });
    cx.run_until_parked();
}

fn draft(pane: &Entity<SessionPane>, cx: &mut VisualTestContext) -> (String, bool) {
    pane.update_in(cx, |pane, window, cx| {
        pane.composer.read(cx).draft_state(window, cx)
    })
}

#[gpui::test]
async fn keys_typed_after_a_rail_pick_land_in_the_picked_sessions_draft(cx: &mut TestAppContext) {
    let (_backend, pane, cx) = pane(cx).await;
    assert_eq!(
        draft(&pane, cx),
        (String::new(), false),
        "the rail has the keys before the pick"
    );
    pick(&pane, SessionOpenTarget::Engine, cx);
    assert_eq!(
        draft(&pane, cx),
        (String::new(), true),
        "the picked session's empty draft takes the keys"
    );
    cx.simulate_input("xyz");
    assert_eq!(
        draft(&pane, cx),
        ("xyz".into(), true),
        "what is typed while the open is on its way is in the draft"
    );
}

#[gpui::test]
async fn a_historical_open_leaves_the_keys_where_they_were(cx: &mut TestAppContext) {
    let (_backend, pane, cx) = pane(cx).await;
    pick(&pane, SessionOpenTarget::Historical, cx);
    assert_eq!(
        draft(&pane, cx),
        (String::new(), false),
        "a read-only open has no draft to type in"
    );
}
