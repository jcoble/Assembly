use super::tests::{CurrentFiles, Host, key};
use super::{SocketClient, WorkspaceCoordinator, WorkspacePanes, WorkspaceSaved, WorkspaceTarget};
use crate::core::socket::{
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use serde_json::json;
use std::{
    cell::RefCell,
    rc::Rc,
    sync::{Arc, Mutex},
};
#[gpui::test]
async fn actual_view_changes_save_through_the_same_cas_owner_without_a_selection_switch(
    cx: &mut TestAppContext,
) {
    let stored = Arc::new(Mutex::new((0u64, None, Vec::new())));
    let state = stored.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return Vec::new();
        }
        let mut state = state.lock().unwrap();
        state.2.push(frame.clone());
        let result = if frame["method"] == "save" {
            assert_eq!(frame["args"]["expectedRevision"].as_u64(), Some(state.0));
            state.0 += 1;
            state.1 = Some(frame["args"]["snapshot"].clone());
            json!({"revision":state.0})
        } else {
            json!({"revision":state.0,"snapshot":state.1})
        };
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
    cx.executor().allow_parking();
    let live = Rc::new(RefCell::new(json!({"files":[]})));
    let coordinator = cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start_bulk(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
        WorkspacePanes::register(Box::new(CurrentFiles(live.clone())), cx).unwrap();
        WorkspaceCoordinator::init(cx)
    });
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let (sent, mut activated) = unbounded();
    let _activated = cx.update(|_, cx| {
        cx.subscribe(
            &coordinator,
            move |_, event: &super::WorkspaceActivated, _| {
                sent.unbounded_send(event.clone()).unwrap();
            },
        )
    });
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                WorkspaceTarget::Checkout(key("/a")),
                Box::new(|_, _, _| Ok(())),
                window,
                cx,
            );
        })
    });
    activated.recv().await.unwrap();
    let (sent, mut saved) = unbounded();
    let _saved = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceSaved, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    *live.borrow_mut() = json!({"files":[],"activePath":"actual user change"});
    cx.update(|_, cx| WorkspaceCoordinator::changed(cx));
    let event = saved.recv().await.unwrap();
    assert_eq!(event.key, key("/a"));
    assert_eq!(event.revision, 1);
    assert_eq!(
        coordinator.read_with(cx, |view, _| view.current_key().cloned()),
        Some(key("/a"))
    );
    let state = stored.lock().unwrap();
    assert_eq!(
        state.1.as_ref().unwrap()["activePath"],
        "actual user change"
    );
    assert_eq!(
        state
            .2
            .iter()
            .filter(|frame| frame["method"] == "read")
            .count(),
        1
    );
    assert_eq!(
        state
            .2
            .iter()
            .filter(|frame| frame["method"] == "save")
            .count(),
        1
    );
}
