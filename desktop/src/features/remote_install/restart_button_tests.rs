//! The button against a fake backend: it's there only with an update ready, and pressing it is what asks for the restart.

use std::sync::{Arc, Mutex};

use futures_channel::mpsc::{UnboundedSender, unbounded};
use gpui::TestAppContext;
use gpui_kit::{AppContext as _, Modifiers, VisualTestContext, px, size};
use serde_json::{Value, json};

use super::RestartButton;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use crate::core::state::machines::Machines;

fn here(ready: Option<&str>) -> Value {
    json!({"name": null, "kind": "local", "reachable": true, "reason": null, "checked": true,
        "restartReady": ready})
}

/// A backend whose `test/ready` sends this machine with an update ready, and that remembers what it's asked.
fn backend(asked: Arc<Mutex<Vec<String>>>, tell: UnboundedSender<()>) -> FakeBackend {
    let sub = Arc::new(Mutex::new(Value::Null));
    FakeBackend::start(move |frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        let mut sub = sub.lock().unwrap();
        let id = frame["id"].clone();
        match (frame["t"].as_str(), frame["method"].as_str()) {
            (Some("sub"), _) => {
                *sub = id.clone();
                vec![Reply::Frame(json!({"t": "state", "sub": id, "items": [
                    {"key": "local", "item": here(None)}]}))]
            }
            (Some("call"), Some("ready")) => vec![
                Reply::Frame(
                    json!({"t": "change", "sub": *sub, "key": "local", "item": here(Some("2.0.0"))}),
                ),
                Reply::Frame(json!({"t": "result", "id": id, "result": {}})),
            ],
            (Some("call"), Some("waiting")) => vec![
                Reply::Frame(json!({"t": "change", "sub": *sub, "key": "local", "item": {
                    "name": null, "kind": "local", "reachable": true, "reason": null, "checked": true,
                    "restartReady": "2.0.0", "updating": true, "updateNote": "Waiting for running sessions to finish…"}})),
                Reply::Frame(json!({"t": "result", "id": id, "result": {}})),
            ],
            (Some("call"), Some(method)) => {
                asked.lock().unwrap().push(format!(
                    "{}/{method}",
                    frame["feature"].as_str().unwrap_or("")
                ));
                _ = tell.unbounded_send(());
                vec![Reply::Frame(
                    json!({"t": "result", "id": id, "result": {"started": true}}),
                )]
            }
            _ => Vec::new(),
        }
    })
}

fn draw(cx: &mut VisualTestContext) {
    cx.update(|window, cx| window.draw(cx).clear(cx));
}

#[gpui::test]
async fn it_appears_when_an_update_is_ready_and_only_a_press_asks_for_the_restart(
    cx: &mut TestAppContext,
) {
    cx.executor().allow_parking();
    let (changed, mut changes) = unbounded();
    let asked = Arc::new(Mutex::new(Vec::new()));
    let backend = backend(asked.clone(), changed.clone());
    cx.update(gpui_kit::init);
    // As in the app: the window is up before the backend's first ready line starts the machines list.
    let made = Arc::new(Mutex::new(None));
    let keep = made.clone();
    let (_, cx) = cx.add_window_view(move |window, cx| {
        let button = cx.new(RestartButton::new);
        *keep.lock().unwrap() = Some(button.clone());
        gpui_kit::base::Root::new(button, window, cx)
    });
    let button = made.lock().unwrap().clone().expect("the button");
    cx.simulate_resize(size(px(400.), px(100.)));
    cx.update(|_, cx| {
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint, cx);
        Machines::start(cx);
    });
    let tell = changed.clone();
    let machines = cx.update(|_, cx| Machines::try_global(cx).expect("the machines"));
    let _heard = cx.update(|_, cx| cx.observe(&machines, move |_, _| _ = tell.unbounded_send(())));

    until("listed", &mut changes, &changed, || {
        cx.update(|_, cx| machines.read(cx).machines().count() == 1)
    })
    .await;
    draw(cx);
    assert!(
        cx.debug_bounds("restart-to-update").is_none(),
        "nothing is ready yet"
    );

    let call = cx.update(|_, cx| {
        SocketClient::global(cx)
            .read(cx)
            .call::<Value>("test", "ready", json!({}), None)
    });
    _ = call.await;
    until("ready", &mut changes, &changed, || {
        cx.update(|_, cx| {
            machines
                .read(cx)
                .machines()
                .any(|machine| machine.restart_ready.is_some())
        })
    })
    .await;
    draw(cx);
    let bounds = cx.debug_bounds("restart-to-update").expect("the button");
    assert!(asked.lock().unwrap().is_empty(), "not before the press");

    cx.simulate_click(bounds.center(), Modifiers::none());
    until("asked", &mut changes, &changed, || {
        !asked.lock().unwrap().is_empty()
    })
    .await;
    assert_eq!(*asked.lock().unwrap(), ["updates/restart"]);
    assert_eq!(
        cx.update(|_, cx| button.read(cx).words().to_owned()),
        "Restart to Update"
    );

    // Pressed, and held up by a running turn: the button says so rather than only spinning.
    let call = cx.update(|_, cx| {
        SocketClient::global(cx)
            .read(cx)
            .call::<Value>("test", "waiting", json!({}), None)
    });
    _ = call.await;
    until("waiting", &mut changes, &changed, || {
        cx.update(|_, cx| machines.read(cx).machines().any(|machine| machine.updating))
    })
    .await;
    assert_eq!(
        cx.update(|_, cx| button.read(cx).words().to_owned()),
        "Waiting for running sessions to finish…"
    );
}
