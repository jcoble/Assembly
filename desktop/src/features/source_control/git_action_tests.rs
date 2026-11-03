use super::GitAction;
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
use serde_json::json;
use std::sync::{Arc, Mutex};
// Pins: g9#10
#[gpui::test]
async fn a_pending_write_refuses_a_second_write_and_a_lost_reply_is_never_replayed(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(0));
    let observed = seen.clone();
    let (changed, mut changes) = unbounded();
    let heard = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        if frame["t"] == "call" {
            *observed.lock().unwrap() += 1;
            let _ = heard.unbounded_send(());
        }
        Vec::<Reply>::new()
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let action = cx.new(|_| GitAction::default());
    action.update(cx, |action, cx| {
        assert!(action.run(
            "s1".into(),
            "stage",
            json!({"session":"s1","paths":["a"]}),
            cx
        ));
        assert!(!action.run(
            "s1".into(),
            "commit",
            json!({"session":"s1","message":"must not send"}),
            cx
        ));
    });
    until("one write received", &mut changes, &changed, || {
        cx.run_until_parked();
        *seen.lock().unwrap() == 1
    })
    .await;
    assert_eq!(*seen.lock().unwrap(), 1);
    drop(backend);
    cx.run_until_parked();
    assert_eq!(*seen.lock().unwrap(), 1);
}
