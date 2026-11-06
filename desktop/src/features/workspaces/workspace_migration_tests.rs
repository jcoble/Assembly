use super::super::WorkspaceTarget;
use super::super::tests::Host;
use super::{
    CenterPanes, FileView, Outcome, Read, SavedEntry, SavedMany, SocketClient,
    WorkspaceCoordinator, WorkspaceKey, WorkspacePanes, confirmed, legacy_migration,
};
use crate::core::socket::{
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use futures_channel::mpsc::unbounded;
use gpui::{TestAppContext, VisualTestContext};
use gpui_kit::component::dock::{BasePanel, DockArea, DockPlacement, panel_handle};
use gpui_kit::{AppContext as _, Entity};
use serde_json::Value;
use serde_json::json;
use std::sync::{Arc, Mutex};
#[derive(Default)]
struct Stored {
    calls: Vec<Value>,
    snapshot: Option<Value>,
}
#[derive(Clone, Copy)]
enum ReceiptKind {
    Confirmed,
    Missing,
    CommittedMissing,
    CanonicalBudgetRefused,
}
async fn legacy(
    cx: &mut TestAppContext,
    receipt: ReceiptKind,
) -> (
    FakeBackend,
    Arc<Mutex<Stored>>,
    Entity<DockArea>,
    Entity<WorkspaceCoordinator>,
    &mut VisualTestContext,
) {
    let stored = Arc::new(Mutex::new(Stored::default()));
    let seen = stored.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return Vec::new();
        }
        let mut stored = seen.lock().unwrap();
        stored.calls.push(frame.clone());
        let result = match (frame["feature"].as_str(), frame["method"].as_str()) {
            (Some("files"), Some("owner")) => {
                json!({"machine":"actual-owner","checkoutRoot":"/real-checkout","remoteSessionId":"resolved-session"})
            }
            (Some("files"), Some("read")) => {
                json!({"content":"disk\n","size":5,"revision":"real-revision","lineCount":1,"binary":false,"nextOffset":null})
            }
            (Some("files"), Some("release")) => json!({}),
            (Some("workspaces"), Some("read")) => {
                json!({"revision":if stored.snapshot.is_some(){1}else{0},"snapshot":stored.snapshot})
            }
            (Some("workspaces"), Some("save_many")) => {
                let saves = frame["args"]["saves"].as_array().unwrap();
                assert_eq!(saves.len(), 1);
                let args = &saves[0];
                assert_eq!(args["machine"], "actual-owner");
                assert_eq!(args["sessionId"], "resolved-session");
                assert_eq!(args["checkoutRoot"], "/real-checkout");
                assert_eq!(args["expectedRevision"], 0);
                if matches!(receipt, ReceiptKind::CanonicalBudgetRefused) {
                    return vec![Reply::Frame(
                        json!({"t":"error","id":frame["id"],"code":"failed","message":"This workspace batch is larger than 3 MiB, so it was not saved."}),
                    )];
                }
                if !matches!(receipt, ReceiptKind::Missing) {
                    stored.snapshot = Some(args["snapshot"].clone());
                }
                if matches!(receipt, ReceiptKind::Confirmed) {
                    json!({"saved":[{"machine":"actual-owner","sessionId":"resolved-session","checkoutRoot":"/real-checkout","revision":1}]})
                } else {
                    json!({"saved":[]})
                }
            }
            _ => panic!("unexpected migration IO: {frame}"),
        };
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        FileView::register(cx);
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint.clone(), cx);
        SocketClient::start_bulk(endpoint, cx);
    });
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let (dock, file) = cx.update(|window, cx| {
        let dock = cx.new(|cx| DockArea::new("legacy", None, window, cx));
        CenterPanes::init(&dock, cx);
        let file = cx.new(|cx| FileView::new("old-alias".into(), "src/λ.rs".into(), cx));
        dock.update(cx, |dock, cx| {
            dock.add_panel_view(
                panel_handle(file.clone()),
                DockPlacement::Center,
                None,
                window,
                cx,
            )
        });
        (dock, file)
    });
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _heard = cx.update(|_, cx| {
        cx.observe(&file, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    until(
        "the actual legacy file owner",
        &mut changes,
        &changed,
        || {
            file.read_with(cx, |file, cx| {
                file.legacy_capture(cx).unwrap().owner.is_some()
            })
        },
    )
    .await;
    let coordinator = cx.update(|_, cx| WorkspaceCoordinator::init(cx));
    (backend, stored, dock, coordinator, cx)
}
#[gpui::test]
async fn real_existing_owner_migrates_with_canonical_cas_before_old_tabs_are_removed(
    cx: &mut TestAppContext,
) {
    let (_backend, stored, _dock, coordinator, cx) = legacy(cx, ReceiptKind::Confirmed).await;
    let (sent, mut activated) = unbounded();
    let _terminal = cx.update(|_, cx| {
        cx.subscribe(
            &coordinator,
            move |_, event: &super::super::WorkspaceActivated, _| {
                sent.unbounded_send(event.clone()).unwrap();
            },
        )
    });
    let captured = stored.clone();
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                WorkspaceTarget::TranscriptOnly(crate::core::engine::SessionKey {
                    machine: "actual-owner".into(),
                    id: "history".into(),
                }),
                Box::new(move |_, _, cx| {
                    assert_eq!(
                        CenterPanes::file_views(cx).len(),
                        1,
                        "old tab survives until canonical confirmation"
                    );
                    assert_eq!(
                        captured.lock().unwrap().snapshot.as_ref().unwrap()["files"][0]["path"],
                        "src/λ.rs"
                    );
                    Ok(())
                }),
                window,
                cx,
            );
        })
    });
    activated.recv().await.unwrap();
    cx.update(|_, cx| assert!(CenterPanes::file_views(cx).is_empty()));
    let stored = stored.lock().unwrap();
    assert_eq!(
        stored
            .calls
            .iter()
            .filter(|frame| frame["method"] == "owner")
            .count(),
        1,
        "existing real owner reused"
    );
    assert_eq!(
        stored
            .calls
            .iter()
            .filter(|frame| frame["feature"] == "workspaces" && frame["method"] == "save_many")
            .count(),
        1
    );
}
#[gpui::test]
async fn an_unconfirmed_migration_write_is_never_replayed_or_allowed_to_remove_old_tabs(
    cx: &mut TestAppContext,
) {
    let (_backend, stored, _dock, coordinator, cx) = legacy(cx, ReceiptKind::Missing).await;
    let (sent, mut refused) = unbounded();
    let _terminal = cx.update(|_, cx| {
        cx.subscribe(
            &coordinator,
            move |_, event: &super::super::WorkspaceRefused, _| {
                sent.unbounded_send(event.clone()).unwrap();
            },
        )
    });
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                WorkspaceTarget::TranscriptOnly(crate::core::engine::SessionKey {
                    machine: "actual-owner".into(),
                    id: "history".into(),
                }),
                Box::new(|_, _, _| panic!("unconfirmed migration cannot depart")),
                window,
                cx,
            );
        })
    });
    assert!(
        refused
            .recv()
            .await
            .unwrap()
            .reason
            .contains("not confirmed")
    );
    cx.update(|_,cx| {
        let files=CenterPanes::file_views(cx);assert_eq!(files.len(),1);
        assert_eq!(serde_json::to_value(BasePanel::dump(files[0].read(cx),cx).info).unwrap()["panel"]["path"],"src/λ.rs");
    });
    let stored = stored.lock().unwrap();
    assert!(stored.snapshot.is_none());
    assert_eq!(
        stored
            .calls
            .iter()
            .filter(|frame| frame["feature"] == "workspaces" && frame["method"] == "save_many")
            .count(),
        1
    );
    assert_eq!(
        stored
            .calls
            .iter()
            .filter(|frame| frame["feature"] == "workspaces" && frame["method"] == "read")
            .count(),
        2,
        "initial read and one exact reconciliation"
    );
}

#[gpui::test]
async fn a_changed_existing_group_refuses_the_whole_batch_and_preserves_every_old_tab(
    cx: &mut TestAppContext,
) {
    use std::collections::HashMap;
    #[derive(Default)]
    struct BatchState {
        snapshots: HashMap<String, (u64, Value)>,
        held: Option<Value>,
        batches: Vec<Value>,
    }
    let state = Arc::new(Mutex::new(BatchState::default()));
    let server = state.clone();
    let (sent, requested) = futures_channel::oneshot::channel();
    let mut sent = Some(sent);
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return Vec::new();
        }
        let mut state = server.lock().unwrap();
        let result = match (frame["feature"].as_str(), frame["method"].as_str()) {
            (Some("files"), Some("owner")) => {
                let alias = frame["args"]["session"].as_str().unwrap();
                json!({"machine":"actual-owner","checkoutRoot":format!("/checkout-{alias}"),"remoteSessionId":alias})
            }
            (Some("files"), Some("read")) => {
                json!({"content":"disk\n","size":5,"revision":"base","lineCount":1,"binary":false,"nextOffset":null})
            }
            (Some("files"), Some("release")) => json!({}),
            (Some("workspaces"), Some("save_many")) => {
                state.batches.push(frame["args"].clone());
                state.held = Some(frame.clone());
                if let Some(sent) = sent.take() {
                    sent.send(()).unwrap();
                }
                return Vec::new();
            }
            (Some("workspaces"), Some("read")) => {
                let session = frame["args"]["sessionId"].as_str().unwrap();
                let value = state
                    .snapshots
                    .get(session)
                    .map(|(revision, snapshot)| json!({"revision":revision,"snapshot":snapshot}))
                    .unwrap_or_else(|| json!({"revision":0,"snapshot":null}));
                if let Some(held) = state.held.take() {
                    // The real backend's all-CAS writer refuses before committing any group. Its rollback is covered by C2.
                    let saves = held["args"]["saves"].as_array().unwrap();
                    assert_eq!(saves.len(), 2);
                    assert!(saves.iter().any(|save| save["expectedRevision"] == 5));
                    assert!(saves.iter().any(|save| save["expectedRevision"] == 0));
                    return vec![
                        Reply::Frame(
                            json!({"t":"error","id":held["id"],"code":"failed","message":"This workspace changed elsewhere; it was not saved."}),
                        ),
                        Reply::Frame(json!({"t":"result","id":frame["id"],"result":value})),
                    ];
                }
                value
            }
            _ => panic!("unexpected migration IO: {frame}"),
        };
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        FileView::register(cx);
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint.clone(), cx);
        SocketClient::start_bulk(endpoint, cx);
    });
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let _dock = cx.update(|window, cx| {
        let dock = cx.new(|cx| DockArea::new("legacy-batch", None, window, cx));
        CenterPanes::init(&dock, cx);
        dock
    });
    let (changed, mut changes) = unbounded();
    let mut observations = Vec::new();
    let mut files = Vec::new();
    // File reads are lazy and owned by the active tab. Resolve the first real owner before
    // opening the second tab, which legitimately cancels unfinished inactive IO.
    for alias in ["one", "two"] {
        let file = cx.update(|window, cx| {
            let file = cx.new(|cx| FileView::new(alias.into(), "src/λ.rs".into(), cx));
            _dock.update(cx, |dock, cx| {
                dock.add_panel_view(
                    panel_handle(file.clone()),
                    DockPlacement::Center,
                    None,
                    window,
                    cx,
                )
            });
            file
        });
        let tell = changed.clone();
        observations.push(cx.update(|_, cx| {
            cx.observe(&file, move |_, _| {
                let _ = tell.unbounded_send(());
            })
        }));
        until(
            "the active legacy file owner is resolved",
            &mut changes,
            &changed,
            || {
                file.read_with(cx, |file, cx| {
                    file.legacy_capture(cx).unwrap().owner.is_some()
                })
            },
        )
        .await;
        files.push(file);
    }
    let groups = cx.update(|_, cx| {
        let captures = CenterPanes::file_views(cx)
            .iter()
            .map(|file| file.read(cx).legacy_capture(cx).unwrap())
            .collect();
        legacy_migration::prepare(captures, &HashMap::new())
            .unwrap()
            .groups
    });
    assert_eq!(groups.len(), 2);
    let existing = groups[0].key.clone();
    let new = groups[1].key.clone();
    state
        .lock()
        .unwrap()
        .snapshots
        .insert(existing.session_id.clone(), (5, groups[0].snapshot.clone()));
    let coordinator = cx.update(|_, cx| WorkspaceCoordinator::init(cx));
    let (sent, mut refused) = unbounded();
    let _terminal = cx.update(|_, cx| {
        cx.subscribe(
            &coordinator,
            move |_, event: &super::super::WorkspaceRefused, _| {
                sent.unbounded_send(event.clone()).unwrap();
            },
        )
    });
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                WorkspaceTarget::TranscriptOnly(crate::core::engine::SessionKey {
                    machine: "actual-owner".into(),
                    id: "history".into(),
                }),
                Box::new(|_, _, _| panic!("conflicting migration must not activate")),
                window,
                cx,
            );
        })
    });
    requested.await.unwrap();
    state.lock().unwrap().snapshots.insert(
        existing.session_id.clone(),
        (
            6,
            json!({"version":1,"files":[{"path":"newer-draft","draft":"preserve me"}]}),
        ),
    );
    // An independent actual read releases the held receipt, with no timer or polling.
    let inspection=cx.update(|_,cx|SocketClient::try_bulk(cx).unwrap().read(cx).call::<Read>("workspaces","read",json!({
        "machine":existing.machine,"sessionId":existing.session_id,"checkoutRoot":existing.checkout_root}),None));
    assert!(matches!(inspection.await, Outcome::Ok(_)));
    assert!(
        refused
            .recv()
            .await
            .unwrap()
            .reason
            .contains("not confirmed")
    );
    cx.update(|_, cx| assert_eq!(CenterPanes::file_views(cx).len(), 2));
    let state = state.lock().unwrap();
    assert_eq!(
        state.batches.len(),
        1,
        "uncertain/conflicting batch is never replayed"
    );
    assert!(
        !state.snapshots.contains_key(&new.session_id),
        "new group is not committed after existing-group CAS fails"
    );
    assert_eq!(state.snapshots[&existing.session_id].0, 6);
    assert_eq!(
        state.snapshots[&existing.session_id].1["files"][0]["draft"],
        "preserve me"
    );
}

include!("workspace_migration_budget_tests.rs");

#[path = "workspace_migration_receipt_tests.rs"]
mod receipt_tests;
