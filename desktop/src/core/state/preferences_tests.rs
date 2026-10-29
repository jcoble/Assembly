//! A save the backend doesn't take never shows as saved: the confirmed value comes back, the failure is said, and the
//! same value can be tried again. With no socket, an edit isn't made at all.

use std::sync::{Arc, Mutex};

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use serde_json::json;

use super::Preferences;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use crate::core::state::appearance::EDITOR_FONT_SIZE;

fn font_size(cx: &mut TestAppContext) -> f32 {
    cx.update(|cx| Preferences::read(cx, |appearance| appearance.number(&EDITOR_FONT_SIZE)))
}

#[gpui::test]
async fn a_save_the_backend_refuses_puts_back_the_saved_value_and_says_so(cx: &mut TestAppContext) {
    let sets = Arc::new(Mutex::new(0));
    let counted = sets.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        let id = frame["id"].clone();
        match (frame["t"].as_str(), frame["method"].as_str()) {
            (Some("sub"), _) => vec![Reply::Frame(json!({"t": "state", "sub": id, "items": [
                {"key": "editor.fontSize", "item": {"key": "editor.fontSize", "value": 16}}]}))],
            (Some("call"), Some("set")) => {
                *counted.lock().unwrap() += 1;
                vec![Reply::Frame(
                    json!({"t": "error", "id": id, "code": "internal", "message": "disk full"}),
                )]
            }
            _ => Vec::new(),
        }
    });
    cx.executor().allow_parking();
    let preferences = cx.update(|cx| {
        gpui_kit::init(cx);
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint, cx);
        Preferences::init(cx);
        Preferences::start(cx);
        Preferences::try_global(cx).expect("the preferences")
    });
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _heard = cx.update(|cx| cx.observe(&preferences, move |_, _| _ = tell.unbounded_send(())));
    until("the saved state", &mut changes, &changed, || {
        font_size(cx) == 16.
    })
    .await;

    cx.update(|cx| Preferences::save(EDITOR_FONT_SIZE.key, json!(18), cx));
    assert_eq!(font_size(cx), 18., "the window follows the edit at once");
    until("the refusal", &mut changes, &changed, || {
        cx.update(|cx| Preferences::failure(cx)).is_some()
    })
    .await;
    assert_eq!(font_size(cx), 16., "the confirmed value is back");
    let failure = cx.update(|cx| Preferences::failure(cx)).unwrap();
    assert!(failure.contains("disk full"), "{failure}");

    // The same value again is a new edit, so it's sent again.
    cx.update(|cx| Preferences::save(EDITOR_FONT_SIZE.key, json!(18), cx));
    until("the retry", &mut changes, &changed, || {
        *sets.lock().unwrap() == 2
    })
    .await;
}

#[gpui::test]
fn with_no_socket_an_edit_isnt_made_and_says_why(cx: &mut TestAppContext) {
    cx.update(|cx| {
        Preferences::init(cx);
        Preferences::save(EDITOR_FONT_SIZE.key, json!(18), cx);
    });
    assert_eq!(font_size(cx), EDITOR_FONT_SIZE.default);
    assert!(cx.update(|cx| Preferences::failure(cx)).is_some());
}

#[gpui::test]
async fn an_older_saves_echo_doesnt_undo_a_newer_edit(cx: &mut TestAppContext) {
    // The backend takes 17 at once and echoes it, and holds 18: the echo of 17 arrives while 18 is on its way.
    let sub = Arc::new(Mutex::new(json!(null)));
    let heard = sub.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        let id = frame["id"].clone();
        match (frame["t"].as_str(), frame["method"].as_str()) {
            (Some("sub"), _) => {
                *heard.lock().unwrap() = id.clone();
                vec![Reply::Frame(json!({"t": "state", "sub": id, "items": [
                    {"key": "editor.fontSize", "item": {"key": "editor.fontSize", "value": 16}}]}))]
            }
            (Some("call"), Some("set")) if frame["args"]["value"] == json!(17) => vec![
                Reply::Frame(json!({"t": "result", "id": id, "result": {}})),
                Reply::Frame(
                    json!({"t": "change", "sub": heard.lock().unwrap().clone(), "key": "editor.fontSize",
                    "item": {"key": "editor.fontSize", "value": 17}}),
                ),
            ],
            _ => Vec::new(),
        }
    });
    cx.executor().allow_parking();
    let preferences = cx.update(|cx| {
        gpui_kit::init(cx);
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint, cx);
        Preferences::init(cx);
        Preferences::start(cx);
        Preferences::try_global(cx).expect("the preferences")
    });
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _heard = cx.update(|cx| cx.observe(&preferences, move |_, _| _ = tell.unbounded_send(())));
    until("the saved state", &mut changes, &changed, || {
        font_size(cx) == 16.
    })
    .await;

    cx.update(|cx| {
        Preferences::save(EDITOR_FONT_SIZE.key, json!(17), cx);
        Preferences::save(EDITOR_FONT_SIZE.key, json!(18), cx);
    });
    until("the echo of 17", &mut changes, &changed, || {
        preferences.read_with(cx, |preferences, _| {
            preferences.confirmed.get(EDITOR_FONT_SIZE.key) == Some(&json!(17))
        })
    })
    .await;
    assert_eq!(font_size(cx), 18., "the newer edit stays");
}
