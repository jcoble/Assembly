use super::{
    Outcome, Read, SocketClient, WorkspaceCoordinator, WorkspaceKey, WorkspacePanes,
    WorkspaceRefused, WorkspaceTarget,
};
use crate::core::socket::{
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use crate::core::state::workspace_panes::PaneCodec;
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::{App, Context, IntoElement, Render, Window, div};
use serde_json::{Value, json};
use std::{
    cell::RefCell,
    rc::Rc,
    sync::{Arc, Mutex},
};

pub(super) struct Host;
impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
struct Files(Rc<RefCell<Vec<Value>>>);
impl PaneCodec for Files {
    fn name(&self) -> &'static str {
        "files"
    }
    fn capture(&self, _: &Window, _: &App) -> Result<Value, String> {
        Ok(json!({"files":[],"activePath":"live-state"}))
    }
    fn validate(&self, _: &Value, _: &WorkspaceKey, _: &Value, _: &App) -> Result<(), String> {
        Ok(())
    }
    fn restore(&self, value: &Value, _: &mut Window, _: &mut App) {
        self.0.borrow_mut().push(value.clone());
    }
}
pub(super) fn key(root: &str) -> WorkspaceKey {
    WorkspaceKey {
        machine: "actual-source".into(),
        session_id: "s".into(),
        checkout_root: root.into(),
    }
}

#[gpui::test]
async fn a_late_initial_read_cannot_restore_the_previous_checkout(cx: &mut TestAppContext) {
    let delayed = Arc::new(Mutex::new(None));
    let held = delayed.clone();
    let (seen, a_seen) = futures_channel::oneshot::channel();
    let seen = Arc::new(Mutex::new(Some(seen)));
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return Vec::new();
        }
        if frame["method"] == "read" && frame["args"]["checkoutRoot"] == "/a" {
            *held.lock().unwrap() = Some(frame["id"].clone());
            if let Some(seen) = seen.lock().unwrap().take() {
                let _ = seen.send(());
            }
            return Vec::new();
        }
        if frame["method"] == "read" {
            let mut replies = Vec::new();
            if let Some(id) = held.lock().unwrap().take() {
                replies.push(Reply::Frame(json!({"t":"result","id":id,"result":{"revision":1,"snapshot":{"version":1,"files":[],"activePath":"old-a"}}})));
            }
            replies.push(Reply::Frame(json!({"t":"result","id":frame["id"],"result":{"revision":2,"snapshot":{"version":1,"files":[],"activePath":"new-b"}}})));
            return replies;
        }
        Vec::new()
    });
    cx.executor().allow_parking();
    let restored = Rc::new(RefCell::new(Vec::new()));
    let coordinator = cx.update(|cx| {
        gpui_kit::init(cx);
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint.clone(), cx);
        SocketClient::start_bulk(endpoint, cx);
        WorkspacePanes::register(Box::new(Files(restored.clone())), cx).unwrap();
        WorkspaceCoordinator::init(cx)
    });
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _heard = cx.update(|_, cx| {
        cx.observe(&coordinator, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    let refused = Rc::new(RefCell::new(Vec::new()));
    let heard = refused.clone();
    let _terminal = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceRefused, _| {
            heard.borrow_mut().push(event.clone())
        })
    });
    let first = cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                WorkspaceTarget::Checkout(key("/a")),
                Box::new(|_, _, _| Ok(())),
                window,
                cx,
            )
        })
    });
    a_seen
        .await
        .expect("the first exact read reached its owning socket");
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                WorkspaceTarget::Checkout(key("/b")),
                Box::new(|_, _, _| Ok(())),
                window,
                cx,
            );
            view.cancel(first, cx);
        })
    });
    until("the latest checkout", &mut changes, &changed, || {
        coordinator.read_with(cx, |view, _| view.current_key() == Some(&key("/b")))
    })
    .await;
    assert_eq!(restored.borrow().len(), 1);
    assert_eq!(restored.borrow()[0]["activePath"], "new-b");
    assert_eq!(refused.borrow().len(), 1);
    assert_eq!(refused.borrow()[0].generation, first);
    assert_eq!(
        refused.borrow()[0].target,
        WorkspaceTarget::Checkout(key("/a"))
    );
    assert!(refused.borrow()[0].reason.contains("superseded"));
}

#[gpui::test]
async fn a_refused_workspace_save_keeps_a_and_never_invokes_the_prepared_b_commit(
    cx: &mut TestAppContext,
) {
    use std::cell::Cell;
    let b_reads = Arc::new(Mutex::new(0));
    let count = b_reads.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return Vec::new();
        }
        if frame["method"] == "save" {
            return vec![Reply::Frame(
                json!({"t":"error","id":frame["id"],"code":"failed","message":"This workspace changed elsewhere; it was not saved."}),
            )];
        }
        if frame["args"]["checkoutRoot"] == "/b" {
            *count.lock().unwrap() += 1;
        }
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":{"revision":5,"snapshot":{"version":1,"files":[],"activePath":"persisted-a"}}}),
        )]
    });
    cx.executor().allow_parking();
    let restored = Rc::new(RefCell::new(Vec::new()));
    let coordinator = cx.update(|cx| {
        gpui_kit::init(cx);
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint.clone(), cx);
        SocketClient::start_bulk(endpoint, cx);
        WorkspacePanes::register(Box::new(Files(restored.clone())), cx).unwrap();
        WorkspaceCoordinator::init(cx)
    });
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _heard = cx.update(|_, cx| {
        cx.observe(&coordinator, move |_, _| {
            let _ = tell.unbounded_send(());
        })
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
    until("A restored", &mut changes, &changed, || {
        coordinator.read_with(cx, |view, _| view.current_key() == Some(&key("/a")))
    })
    .await;
    let committed = Rc::new(Cell::new(0));
    let callback = committed.clone();
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                WorkspaceTarget::Checkout(key("/b")),
                Box::new(move |_, _, _| {
                    callback.set(callback.get() + 1);
                    Ok(())
                }),
                window,
                cx,
            );
        })
    });
    until("the actual refusal", &mut changes, &changed, || {
        coordinator.read_with(cx, |view, _| {
            view.status
                .as_ref()
                .is_some_and(|status| status.starts_with("Not saved:"))
        })
    })
    .await;
    assert_eq!(committed.get(), 0);
    assert_eq!(*b_reads.lock().unwrap(), 0);
    assert_eq!(
        coordinator.read_with(cx, |view, _| view.current_key().cloned()),
        Some(key("/a"))
    );
    assert_eq!(restored.borrow().len(), 1);
}

#[gpui::test]
async fn a_corrupt_target_never_commits_selection_or_overwrites_the_current_key(
    cx: &mut TestAppContext,
) {
    use std::cell::Cell;
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return Vec::new();
        }
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":{"revision":7,"snapshot":{"version":999,"files":[]}}}),
        )]
    });
    cx.executor().allow_parking();
    let restored = Rc::new(RefCell::new(Vec::new()));
    let coordinator = cx.update(|cx| {
        gpui_kit::init(cx);
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start_bulk(endpoint, cx);
        WorkspacePanes::register(Box::new(Files(restored.clone())), cx).unwrap();
        WorkspacePanes::set_key(key("/a"), cx);
        WorkspaceCoordinator::init(cx)
    });
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _heard = cx.update(|_, cx| {
        cx.observe(&coordinator, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    let committed = Rc::new(Cell::new(false));
    let callback = committed.clone();
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                WorkspaceTarget::Checkout(key("/b")),
                Box::new(move |_, _, _| {
                    callback.set(true);
                    Ok(())
                }),
                window,
                cx,
            );
        })
    });
    until("corrupt target refused", &mut changes, &changed, || {
        coordinator.read_with(cx, |view, _| view.status.is_some())
    })
    .await;
    assert!(!committed.get());
    assert!(restored.borrow().is_empty());
    cx.update(|_, cx| assert_eq!(WorkspacePanes::key(cx), Some(&key("/a"))));
}

#[gpui::test]
async fn immediate_refusals_are_delivered_after_registration_once_for_each_generation(
    cx: &mut TestAppContext,
) {
    use std::cell::Cell;
    cx.executor().allow_parking();
    let coordinator = cx.update(|cx| {
        gpui_kit::init(cx);
        WorkspaceCoordinator::init(cx)
    });
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let registered = Rc::new(Cell::new(0));
    let registration = registered.clone();
    let (sent, mut events) = unbounded();
    let _heard = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceRefused, _| {
            assert!(
                registration.get() >= event.generation,
                "request returns before terminal delivery"
            );
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    let first = cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            let generation = view.request(
                WorkspaceTarget::Checkout(key("/a")),
                Box::new(|_, _, _| panic!("missing connection cannot commit")),
                window,
                cx,
            );
            registered.set(generation);
            generation
        })
    });
    let second = cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            let generation = view.request(
                WorkspaceTarget::Checkout(key("/b")),
                Box::new(|_, _, _| panic!("missing connection cannot commit")),
                window,
                cx,
            );
            registered.set(generation);
            view.cancel(first, cx);
            view.cancel(generation, cx);
            generation
        })
    });
    let a = events.recv().await.unwrap();
    let b = events.recv().await.unwrap();
    assert_eq!(
        (a.generation, a.target),
        (first, WorkspaceTarget::Checkout(key("/a")))
    );
    assert_eq!(
        (b.generation, b.target),
        (second, WorkspaceTarget::Checkout(key("/b")))
    );
    assert!(a.reason.contains("unavailable"));
    assert!(b.reason.contains("unavailable"));
    assert!(
        events.try_recv().is_err(),
        "repeated cancellation cannot produce another terminal outcome"
    );
}

#[gpui::test]
async fn cancelling_a_held_read_emits_one_refusal_and_drops_the_prepared_commit(
    cx: &mut TestAppContext,
) {
    let (seen, requested) = futures_channel::oneshot::channel();
    let seen = Arc::new(Mutex::new(Some(seen)));
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
    struct Prepared(Rc<std::cell::Cell<bool>>);
    impl Drop for Prepared {
        fn drop(&mut self) {
            self.0.set(true);
        }
    }
    let dropped = Rc::new(std::cell::Cell::new(false));
    let prepared = Prepared(dropped.clone());
    let generation = cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                WorkspaceTarget::Checkout(key("/a")),
                Box::new(move |_, _, _| {
                    let _prepared = prepared;
                    panic!("cancelled navigation must never commit")
                }),
                window,
                cx,
            )
        })
    });
    requested.await.unwrap();
    cx.update(|_, cx| {
        coordinator.update(cx, |view, cx| {
            view.cancel(generation, cx);
            view.cancel(generation, cx);
        })
    });
    let event = events.recv().await.unwrap();
    assert_eq!(event.generation, generation);
    assert_eq!(event.target, WorkspaceTarget::Checkout(key("/a")));
    assert!(event.reason.contains("cancelled"));
    assert!(dropped.get());
    assert!(events.try_recv().is_err());
    assert!(coordinator.read_with(cx, |view, _| view.current_key().is_none()));
}

pub(super) struct CurrentFiles(pub(super) Rc<RefCell<Value>>);
impl PaneCodec for CurrentFiles {
    fn name(&self) -> &'static str {
        "files"
    }
    fn capture(&self, _: &Window, _: &App) -> Result<Value, String> {
        Ok(self.0.borrow().clone())
    }
    fn validate(&self, value: &Value, _: &WorkspaceKey, _: &Value, _: &App) -> Result<(), String> {
        if value["files"].is_array() {
            Ok(())
        } else {
            Err("Invalid files".into())
        }
    }
    fn restore(&self, value: &Value, _: &mut Window, _: &mut App) {
        *self.0.borrow_mut() = value.clone();
    }
}
#[gpui::test]
async fn a_to_b_to_a_checkouts_restore_each_roots_own_saved_view(cx: &mut TestAppContext) {
    let snapshots = Arc::new(Mutex::new(std::collections::HashMap::from([
        (
            "/a".to_string(),
            (
                2u64,
                json!({"version":1,"files":[],"activePath":"a-original"}),
            ),
        ),
        (
            "/b".to_string(),
            (
                4u64,
                json!({"version":1,"files":[],"activePath":"b-original"}),
            ),
        ),
    ])));
    let stored = snapshots.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return Vec::new();
        }
        let mut stored = stored.lock().unwrap();
        let root = frame["args"]["checkoutRoot"].as_str().unwrap();
        let (revision, snapshot) = stored.get_mut(root).unwrap();
        let result = if frame["method"] == "save" {
            assert_eq!(frame["args"]["expectedRevision"].as_u64(), Some(*revision));
            *revision += 1;
            *snapshot = frame["args"]["snapshot"].clone();
            json!({"revision":revision})
        } else {
            json!({"revision":revision,"snapshot":snapshot})
        };
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
    cx.executor().allow_parking();
    let live = Rc::new(RefCell::new(json!({"files":[]})));
    let coordinator = cx.update(|cx| {
        gpui_kit::init(cx);
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start_bulk(endpoint, cx);
        WorkspacePanes::register(Box::new(CurrentFiles(live.clone())), cx).unwrap();
        WorkspaceCoordinator::init(cx)
    });
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _heard = cx.update(|_, cx| {
        cx.observe(&coordinator, move |_, _| {
            let _ = tell.unbounded_send(());
        })
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
    until("A initially restored", &mut changes, &changed, || {
        coordinator.read_with(cx, |view, _| view.current_key() == Some(&key("/a")))
    })
    .await;
    assert_eq!(live.borrow()["activePath"], "a-original");
    *live.borrow_mut() = json!({"files":[],"activePath":"a-edited"});
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                WorkspaceTarget::Checkout(key("/b")),
                Box::new(|_, _, _| Ok(())),
                window,
                cx,
            );
        })
    });
    until("B restored after A save", &mut changes, &changed, || {
        coordinator.read_with(cx, |view, _| view.current_key() == Some(&key("/b")))
    })
    .await;
    assert_eq!(live.borrow()["activePath"], "b-original");
    *live.borrow_mut() = json!({"files":[],"activePath":"b-edited"});
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
    until("A restored again", &mut changes, &changed, || {
        coordinator.read_with(cx, |view, _| view.current_key() == Some(&key("/a")))
    })
    .await;
    assert_eq!(live.borrow()["activePath"], "a-edited");
    let stored = snapshots.lock().unwrap();
    assert_eq!(stored["/a"].0, 3);
    assert_eq!(stored["/a"].1["activePath"], "a-edited");
    assert_eq!(stored["/b"].0, 5);
    assert_eq!(stored["/b"].1["activePath"], "b-edited");
}

include!("workspace_coordinator_coalescing_tests.rs");
