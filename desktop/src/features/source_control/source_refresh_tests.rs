use super::SourceControlPane;
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use crate::core::state::{selected_session::SelectedSession, sessions::Sessions};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::component::dock::BasePanel as _;
use gpui_kit::{AppContext as _, EmptyView};
use serde_json::json;
use std::sync::{Arc, Mutex};

// Pins: g9#26; observes actual window activation, with no timer.
#[gpui::test]
async fn regaining_focus_refreshes_once_and_a_hidden_pane_reads_nothing(cx: &mut TestAppContext) {
    let reads = Arc::new(Mutex::new(0usize));
    let observed = reads.clone();
    let (changed, mut changes) = unbounded();
    let heard = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] == "sub" {
            return vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[]}),
            )];
        }
        if frame["t"] != "call" {
            return vec![];
        }
        let result = if frame["method"] == "status" {
            *observed.lock().unwrap() += 1;
            json!({"branch":"main","head":"a".repeat(40),"ahead":0,"behind":0,"hasUpstream":false,"files":[],"totalFiles":0,"totalStaged":0,"fingerprint":"actual"})
        } else if frame["method"] == "branches" {
            json!({"remotes":[]})
        } else {
            json!({})
        };
        let _ = heard.unbounded_send(());
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
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
        SelectedSession::global(cx)
            .update(cx, |selected, cx| selected.select(Some("s1".into()), cx));
    });
    let mut other = None;
    let _ = cx.add_window_view(|window, _| {
        other = Some(window.window_handle());
        EmptyView
    });
    let other = other.unwrap();
    let (pane, cx) = cx.add_window_view(|window, cx| SourceControlPane::new(window, cx));
    let tell = changed.clone();
    let _heard = cx.update(|_, cx| {
        cx.observe(&pane, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    cx.update(|window, _| window.activate_window());
    cx.run_until_parked();
    pane.update_in(cx, |pane, window, cx| pane.set_active(true, window, cx));
    until("initial status", &mut changes, &changed, || {
        cx.run_until_parked();
        *reads.lock().unwrap() == 1
    })
    .await;
    cx.update(|window, _| window.activate_window());
    cx.run_until_parked();
    assert_eq!(*reads.lock().unwrap(), 1);
    other
        .update(cx, |_, window, _| window.activate_window())
        .unwrap();
    cx.run_until_parked();
    cx.update(|window, _| window.activate_window());
    until("focus status refresh", &mut changes, &changed, || {
        cx.run_until_parked();
        *reads.lock().unwrap() == 2
    })
    .await;
    pane.update_in(cx, |pane, window, cx| pane.set_active(false, window, cx));
    other
        .update(cx, |_, window, _| window.activate_window())
        .unwrap();
    cx.run_until_parked();
    cx.update(|window, _| window.activate_window());
    cx.run_until_parked();
    assert_eq!(*reads.lock().unwrap(), 2);
}
