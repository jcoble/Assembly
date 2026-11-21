//! The Home panel against a scripted backend: a press on Home opens it, it subscribes, and it shows the state the
//! backend pushes. Wording and layout are checked in the real window, not here.

use std::sync::{Arc, Mutex};

use futures_channel::mpsc::unbounded;
use gpui::{Modifiers, TestAppContext};
use serde_json::{Value, json};

use super::home_button::HomeButton;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;

/// One pushed answer: Codex needs a sign-in on the remote, so the backend says so in the summary.
fn pushed() -> Value {
    json!({
        "summary": {"kind": "needs_sign_in", "message": "Codex needs sign-in on Workbox."},
        "providers": [{
            "id": "codex", "name": "Codex", "glyph": "icons/square-terminal.svg", "vendorFamily": "codex",
            "enabled": true, "state": "needs_sign_in", "models": null,
            "machines": [
                {"machineKey": "local", "state": "ready", "installed": true, "cliVersion": "0.42.0",
                 "models": [{"id": "gpt-5.4", "name": "GPT-5.4"}], "lastFailure": null},
                {"machineKey": "Workbox", "state": "needs_sign_in", "installed": true, "models": null,
                 "lastFailure": {"message": "Codex isn't signed in on Workbox.", "detail": "auth_required"}}
            ]
        }],
        "machines": [
            {"key": "local", "displayName": "This Mac", "connectionState": "connected", "updateState": "up_to_date",
             "waitingOnSessions": false, "machine": {"key": "local", "name": null, "reachable": true}},
            {"key": "Workbox", "displayName": "Workbox", "connectionState": "connected", "updateState": "up_to_date",
             "waitingOnSessions": false, "machine": {"key": "Workbox", "name": "Workbox", "reachable": true}}
        ]
    })
}

#[gpui::test]
async fn a_press_on_home_opens_the_panel_showing_the_pushed_state(cx: &mut TestAppContext) {
    // The backend answers the panel's subscription with one pushed item, and notes each frame it gets.
    let frames = Arc::new(Mutex::new(Vec::<Value>::new()));
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let backend = {
        let frames = frames.clone();
        FakeBackend::start(move |frame| {
            if let Some(replies) = hello(frame) {
                return replies;
            }
            frames.lock().unwrap().push(frame.clone());
            _ = tell.unbounded_send(());
            match frame["t"].as_str() {
                Some("sub") if frame["topic"] == "settings_state" => vec![Reply::Frame(
                    json!({"t":"state","sub":frame["id"],"items":[{"key":"current","item":pushed()}]}),
                )],
                _ => Vec::new(),
            }
        })
    };
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
    let (home, cx) = cx.add_window_view(|_, _| HomeButton::default());
    cx.run_until_parked();
    let subscribed = |frames: &Arc<Mutex<Vec<Value>>>| {
        frames
            .lock()
            .unwrap()
            .iter()
            .any(|frame| frame["t"] == "sub")
    };
    assert!(!subscribed(&frames), "a closed panel subscribes to nothing");

    // A press on Home opens the panel, which subscribes and shows what came.
    let button = cx.debug_bounds("home-button").expect("Home is drawn");
    cx.simulate_click(button.center(), Modifiers::none());
    until("the pushed state shown", &mut changes, &changed, || {
        cx.run_until_parked();
        let panel = cx.read(|cx| home.read(cx).panel());
        panel.is_some_and(|panel| cx.read(|cx| panel.read(cx).state.is_some()))
    })
    .await;
    let panel = cx
        .read(|cx| home.read(cx).panel())
        .expect("the panel is open");
    let state = cx
        .read(|cx| panel.read(cx).state.clone())
        .expect("a pushed state");
    assert_eq!(state.summary.message, "Codex needs sign-in on Workbox.");
    assert_eq!(state.providers[0].state.as_deref(), Some("needs_sign_in"));
    assert_eq!(
        state.providers[0].machines[1].state.as_deref(),
        Some("needs_sign_in")
    );
    assert!(
        cx.debug_bounds("home-panel").is_some(),
        "the panel is drawn"
    );
    assert!(
        cx.debug_bounds("home-provider-codex").is_some(),
        "with the provider's row"
    );
    let sub = frames
        .lock()
        .unwrap()
        .iter()
        .find(|frame| frame["t"] == "sub")
        .cloned()
        .unwrap();
    assert_eq!(
        (sub["topic"].as_str(), sub["key"].as_str()),
        (Some("settings_state"), Some("current"))
    );
}
