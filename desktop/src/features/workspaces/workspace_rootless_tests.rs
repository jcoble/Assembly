use super::tests::{CurrentFiles, Host, key};
use super::{
    SocketClient, WorkspaceActivated, WorkspaceCoordinator, WorkspaceKey, WorkspacePanes,
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
use gpui_kit::{App, AppContext as _, Window};
use serde_json::{Value, json};
use std::{
    cell::{Cell, RefCell},
    rc::Rc,
    sync::{Arc, Mutex},
};

#[gpui::test]
async fn rootless_transcript_activation_does_no_incoming_workspace_or_file_io(
    cx: &mut TestAppContext,
) {
    let calls = Arc::new(Mutex::new(Vec::new()));
    let seen = calls.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] == "call" {
            seen.lock().unwrap().push(frame.clone());
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
    let (sent, mut outcomes) = unbounded();
    let _heard = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceActivated, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    let target = WorkspaceTarget::TranscriptOnly(crate::core::engine::SessionKey {
        machine: "actual-source".into(),
        id: "historical".into(),
    });
    let committed = Rc::new(std::cell::Cell::new(false));
    let commit = committed.clone();
    let expected = target.clone();
    let generation = cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                target.clone(),
                Box::new(move |target, _, cx| {
                    assert_eq!(target, &expected);
                    assert!(WorkspacePanes::key(cx).is_none());
                    commit.set(true);
                    Ok(())
                }),
                window,
                cx,
            )
        })
    });
    let activated = outcomes.recv().await.unwrap();
    assert_eq!(activated.generation, generation);
    assert_eq!(activated.target, target);
    assert!(committed.get());
    assert!(calls.lock().unwrap().is_empty());
    assert!(coordinator.read_with(cx, |view, _| view.current_key().is_none()));
}
#[gpui::test]
async fn rootless_departure_still_requires_the_outgoing_exact_cas_save(cx: &mut TestAppContext) {
    let reads = Arc::new(Mutex::new(Vec::new()));
    let read = reads.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return Vec::new();
        }
        assert_eq!(frame["feature"], "workspaces");
        assert_eq!(frame["args"]["checkoutRoot"], "/a");
        if frame["method"] == "save" {
            return vec![Reply::Frame(
                json!({"t":"error","id":frame["id"],"code":"failed","message":"Revision conflict"}),
            )];
        }
        read.lock().unwrap().push(frame.clone());
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":{"revision":5,"snapshot":{"version":1,"files":[]}}}),
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
    until(
        "outgoing workspace hydrated",
        &mut changes,
        &changed,
        || coordinator.read_with(cx, |view, _| view.current_key() == Some(&key("/a"))),
    )
    .await;
    *live.borrow_mut() = json!({"files":[],"activePath":"newer draft metadata"});
    let (sent, mut refused) = unbounded();
    let _terminal = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceRefused, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    let target = WorkspaceTarget::TranscriptOnly(crate::core::engine::SessionKey {
        machine: "actual-source".into(),
        id: "historical".into(),
    });
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                target.clone(),
                Box::new(|_, _, _| panic!("refused outgoing CAS must never commit transcript")),
                window,
                cx,
            );
        })
    });
    let terminal = refused.recv().await.unwrap();
    assert_eq!(terminal.target, target);
    assert!(terminal.reason.contains("Revision conflict"));
    assert_eq!(
        reads.lock().unwrap().len(),
        2,
        "only outgoing hydration and exact save reconciliation"
    );
    assert_eq!(live.borrow()["activePath"], "newer draft metadata");
    assert_eq!(
        coordinator.read_with(cx, |view, _| view.current_key().cloned()),
        Some(key("/a"))
    );
}

struct OwnedFiles {
    live: Rc<RefCell<Value>>,
    alive: Rc<Cell<bool>>,
}
impl PaneCodec for OwnedFiles {
    fn name(&self) -> &'static str {
        "files"
    }
    fn capture(&self, _: &Window, _: &App) -> Result<Value, String> {
        Ok(self.live.borrow().clone())
    }
    fn validate(&self, _: &Value, _: &WorkspaceKey, _: &Value, _: &App) -> Result<(), String> {
        Ok(())
    }
    fn restore(&self, value: &Value, _: &mut Window, _: &mut App) {
        *self.live.borrow_mut() = value.clone();
        self.alive.set(true);
    }
    fn deactivate(&self, _: &mut Window, _: &mut App) {
        self.alive.set(false);
    }
}
#[gpui::test]
async fn checkout_to_transcript_saves_before_teardown_and_return_restores_that_checkout(
    cx: &mut TestAppContext,
) {
    let saved = Arc::new(Mutex::new((
        2u64,
        json!({"version":1,"files":[],"activePath":"original"}),
        Vec::new(),
    )));
    let actual = saved.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return Vec::new();
        }
        assert_eq!(frame["feature"], "workspaces");
        assert_eq!(frame["args"]["checkoutRoot"], "/a");
        let mut saved = actual.lock().unwrap();
        saved.2.push(frame["method"].clone());
        let result = if frame["method"] == "save" {
            assert_eq!(frame["args"]["expectedRevision"], saved.0);
            saved.0 += 1;
            saved.1 = frame["args"]["snapshot"].clone();
            json!({"revision":saved.0})
        } else {
            json!({"revision":saved.0,"snapshot":saved.1})
        };
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
    cx.executor().allow_parking();
    let live = Rc::new(RefCell::new(Value::Null));
    let alive = Rc::new(Cell::new(false));
    let coordinator = cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start_bulk(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
        WorkspacePanes::register(
            Box::new(OwnedFiles {
                live: live.clone(),
                alive: alive.clone(),
            }),
            cx,
        )
        .unwrap();
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
    until("A loaded", &mut changes, &changed, || {
        coordinator.read_with(cx, |view, _| view.current_key() == Some(&key("/a")))
    })
    .await;
    *live.borrow_mut() = json!({"files":[],"activePath":"edited A"});
    let target = WorkspaceTarget::TranscriptOnly(crate::core::engine::SessionKey {
        machine: "actual-source".into(),
        id: "history".into(),
    });
    let committed = Rc::new(Cell::new(false));
    let commit = committed.clone();
    let owned = alive.clone();
    let receipt = saved.clone();
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                target,
                Box::new(move |_, _, cx| {
                    assert_eq!(
                        receipt.lock().unwrap().0,
                        3,
                        "outgoing save is confirmed first"
                    );
                    assert!(owned.get(), "teardown follows the staged commit");
                    assert!(WorkspacePanes::key(cx).is_none());
                    commit.set(true);
                    Ok(())
                }),
                window,
                cx,
            );
        })
    });
    until("transcript committed", &mut changes, &changed, || {
        committed.get()
    })
    .await;
    assert!(!alive.get());
    assert_eq!(saved.lock().unwrap().2, vec![json!("read"), json!("save")]);
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
    until(
        "A restored after transcript",
        &mut changes,
        &changed,
        || coordinator.read_with(cx, |view, _| view.current_key() == Some(&key("/a"))),
    )
    .await;
    assert!(alive.get());
    assert_eq!(live.borrow()["activePath"], "edited A");
    assert_eq!(
        saved.lock().unwrap().2,
        vec![json!("read"), json!("save"), json!("read")]
    );
}
#[gpui::test]
async fn transcript_commit_refusal_restores_the_previous_key_and_never_deactivates(
    cx: &mut TestAppContext,
) {
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return Vec::new();
        }
        let result = if frame["method"] == "save" {
            json!({"revision":2})
        } else {
            json!({"revision":1,"snapshot":{"version":1,"files":[],"activePath":"A"}})
        };
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
    cx.executor().allow_parking();
    let live = Rc::new(RefCell::new(Value::Null));
    let alive = Rc::new(Cell::new(false));
    let coordinator = cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start_bulk(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
        WorkspacePanes::register(
            Box::new(OwnedFiles {
                live: live.clone(),
                alive: alive.clone(),
            }),
            cx,
        )
        .unwrap();
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
    until("A loaded", &mut changes, &changed, || {
        coordinator.read_with(cx, |view, _| view.current_key() == Some(&key("/a")))
    })
    .await;
    let (sent, mut refused) = unbounded();
    let _terminal = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceRefused, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                WorkspaceTarget::TranscriptOnly(crate::core::engine::SessionKey {
                    machine: "actual-source".into(),
                    id: "history".into(),
                }),
                Box::new(|_, _, _| Err("Prepared transcript was superseded".into())),
                window,
                cx,
            );
        })
    });
    assert!(refused.recv().await.unwrap().reason.contains("superseded"));
    assert!(alive.get());
    assert_eq!(live.borrow()["activePath"], "A");
    cx.update(|_, cx| assert_eq!(WorkspacePanes::key(cx), Some(&key("/a"))));
    assert_eq!(
        coordinator.read_with(cx, |view, _| view.current_key().cloned()),
        Some(key("/a"))
    );
}

#[gpui::test]
async fn unmigrated_legacy_file_metadata_blocks_both_checkout_and_rootless_teardown(
    cx: &mut TestAppContext,
) {
    use crate::core::ui::center_panes::CenterPanes;
    use crate::features::files::file_view::FileView;
    use gpui_kit::component::dock::{BasePanel, DockArea, DockPlacement, panel_handle};
    cx.update(gpui_kit::init);
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let file = cx.update(|window, cx| {
        let dock = cx.new(|cx| DockArea::new("migration", None, window, cx));
        CenterPanes::init(&dock, cx);
        let file = cx.new(|cx| FileView::new("old-actual-session".into(), "src/λ.rs".into(), cx));
        dock.update(cx, |dock, cx| {
            dock.add_panel_view(
                panel_handle(file.clone()),
                DockPlacement::Center,
                None,
                window,
                cx,
            )
        });
        // The dock is retained for the actual duration of the migration test.
        (dock, file)
    });
    let coordinator = cx.update(|_, cx| WorkspaceCoordinator::init(cx));
    let (sent, mut outcomes) = unbounded();
    let _heard = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceRefused, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    for target in [
        WorkspaceTarget::Checkout(key("/a")),
        WorkspaceTarget::TranscriptOnly(crate::core::engine::SessionKey {
            machine: "actual-source".into(),
            id: "history".into(),
        }),
    ] {
        cx.update(|window, cx| {
            coordinator.update(cx, |view, cx| {
                view.request(
                    target.clone(),
                    Box::new(|_, _, _| panic!("unmigrated metadata must not be removed")),
                    window,
                    cx,
                );
            })
        });
        let refused = outcomes.recv().await.unwrap();
        assert_eq!(refused.target, target);
        assert!(refused.reason.contains("migrated"));
        cx.update(|_, cx| {
            assert_eq!(CenterPanes::file_views(cx).len(), 1);
            let saved = BasePanel::dump(file.1.read(cx), cx);
            assert_eq!(
                serde_json::to_value(saved.info).unwrap()["panel"]["path"],
                "src/λ.rs"
            );
        });
    }
}
