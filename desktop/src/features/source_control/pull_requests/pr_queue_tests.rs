use super::{PrQueue, QueueState};
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

#[gpui::test]
async fn the_queue_reads_only_when_open_pages_by_cursor_and_changes_scope_once(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, mut changes) = unbounded();
    let sent = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return vec![];
        }
        assert_eq!(frame["feature"], "git");
        assert_eq!(frame["method"], "prs");
        observed.lock().unwrap().push(frame.clone());
        let second = frame["args"]["cursor"] == "next";
        let _ = sent.unbounded_send(());
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":{"items":[{"repository":"o/r","number":if second{31}else{1},"title":"PR"}],"cursor":if second{Value::Null}else{json!("next")},"total":31}}),
        )]
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
    let queue = cx.new(|_| PrQueue::new(None, vec!["o/r".into()]));
    cx.run_until_parked();
    assert!(seen.lock().unwrap().is_empty());
    let sent = changed.clone();
    let _observe = cx.update(|cx| {
        cx.observe(&queue, move |_, _| {
            let _ = sent.unbounded_send(());
        })
    });
    queue.update(cx, |q, cx| q.set_active(true, cx));
    until("first PR page", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| queue.read(cx).page.items.len() == 1)
    })
    .await;
    queue.update(cx, |q, cx| {
        q.load_more(cx);
        q.load_more(cx);
    });
    until("second PR page", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| queue.read(cx).page.items.len() == 2)
    })
    .await;
    assert_eq!(seen.lock().unwrap().len(), 2);
    assert_eq!(seen.lock().unwrap()[1]["args"]["cursor"], "next");
    queue.update(cx, |q, cx| q.select_state(QueueState::Review, cx));
    until("review scope", &mut changes, &changed, || {
        cx.run_until_parked();
        seen.lock().unwrap().len() == 3
    })
    .await;
    assert_eq!(seen.lock().unwrap()[2]["args"]["state"], "review");
    queue.update(cx, |q, cx| q.set_active(false, cx));
    cx.run_until_parked();
    assert_eq!(seen.lock().unwrap().len(), 3);
}
