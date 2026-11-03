use super::{FolderBrowser, FolderChosen};
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
async fn browser_uses_the_selected_machine_and_selects_only_the_returned_folder(
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
        assert_eq!(frame["feature"], "projects");
        assert_eq!(frame["method"], "folders");
        observed.lock().unwrap().push(frame.clone());
        let _ = sent.unbounded_send(());
        let path = if frame["args"]["path"] == "/remote/child" {
            "/remote/child"
        } else {
            "/remote"
        };
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":{"path":path,"directories":["/remote/child"],"truncated":false}}),
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
    let browser = cx.new(|cx| FolderBrowser::new("workbox-test".into(), "/remote".into(), cx));
    let sent = changed.clone();
    let _observe = cx.update(|cx| {
        cx.observe(&browser, move |_, _| {
            let _ = sent.unbounded_send(());
        })
    });
    until("remote folders", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| browser.read(cx).listing.is_some())
    })
    .await;
    assert_eq!(seen.lock().unwrap()[0]["args"]["machine"], "workbox-test");
    let chosen = Arc::new(Mutex::new(None));
    let captured = chosen.clone();
    let _select = cx.update(|cx| {
        cx.subscribe(&browser, move |_, event: &FolderChosen, _| {
            *captured.lock().unwrap() = Some((event.machine.clone(), event.path.clone()));
        })
    });
    browser.update(cx, |browser, cx| browser.browse("/remote/child".into(), cx));
    browser.update(cx, |browser, cx| browser.choose(cx));
    assert!(chosen.lock().unwrap().is_none());
    until("child folders", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| {
            browser
                .read(cx)
                .listing
                .as_ref()
                .is_some_and(|listing| listing.path == "/remote/child")
        })
    })
    .await;
    browser.update(cx, |browser, cx| browser.choose(cx));
    assert_eq!(
        *chosen.lock().unwrap(),
        Some(("workbox-test".into(), "/remote/child".into()))
    );
}
