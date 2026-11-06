//! A cancelled open: the navigator that asked for it has moved on (a newer session was clicked), so it ends quietly.
use super::tests::{Host, key};
use super::{SocketClient, WorkspaceCoordinator, WorkspaceRefused, WorkspaceTarget};
use crate::core::socket::{
    endpoint::Endpoint,
    fake_backend::{FakeBackend, hello},
};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use std::sync::{Arc, Mutex};

#[gpui::test]
async fn a_cancelled_open_tells_its_navigator_and_shows_nothing(cx: &mut TestAppContext) {
    let (seen, requested) = futures_channel::oneshot::channel();
    let seen = Arc::new(Mutex::new(Some(seen)));
    // The checkout's saved view is still being read when the open is cancelled.
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] == "call"
            && frame["method"] == "read"
            && let Some(seen) = seen.lock().unwrap().take()
        {
            let _ = seen.send(());
        }
        Vec::new()
    });
    cx.executor().allow_parking();
    let coordinator = cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start_bulk(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
        WorkspaceCoordinator::init(cx)
    });
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let (sent, mut events) = unbounded();
    let _heard = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceRefused, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    let generation = cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                WorkspaceTarget::Checkout(key("/a")),
                Box::new(|_, _, _| panic!("a cancelled open never commits")),
                window,
                cx,
            )
        })
    });
    requested.await.unwrap();
    cx.update(|_, cx| coordinator.update(cx, |view, cx| view.cancel(generation, cx)));
    let event = events.recv().await.unwrap();
    assert_eq!(event.generation, generation);
    assert_eq!(
        coordinator.read_with(cx, |view, _| view.status.clone()),
        None,
        "nothing went wrong that the user needs to read"
    );
}
