//! Opening a session whose saved workspace has an active file, through the real file and center tab codecs.
use super::{
    SocketClient, WorkspaceActivated, WorkspaceCoordinator, WorkspaceKey, WorkspaceRefused,
    WorkspaceSaved, WorkspaceTarget,
};
use crate::core::socket::{
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use crate::core::state::{
    preferences::Preferences, selected_session::SelectedSession, workspace_panes::WorkspacePanes,
};
use crate::core::ui::center_panes::CenterPanes;
use crate::features::changes::changes_pane::ChangesPane;
use crate::features::files::file_view::FileView;
use crate::features::session::view::session_pane::SessionPane;
use crate::features::shell::default_layout::default_state;
use crate::features::source_control::{history_pane::HistoryPane, pull_requests::pr_pane::PrPane};
use crate::features::workspaces::top_tabs_codec::TopTabsCodec;
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::component::dock::{BasePanel, DockArea, DockPlacement, PaneRef};
use gpui_kit::{
    App, AppContext as _, Context, Entity, IntoElement, Render, Subscription, Window, div,
};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

struct Host;
impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
fn owner() -> WorkspaceKey {
    WorkspaceKey {
        machine: "actual-owner".into(),
        session_id: "session".into(),
        checkout_root: "/checkout".into(),
    }
}
/// The shape a session saves after a person read a file there: one clean file, shown, beside a commit's Changes tab.
fn saved() -> Value {
    let document = r#"document:["actual-owner","/checkout","README.md"]"#;
    json!({
        "version": 1,
        "files": [{"path": "README.md", "baseRevision": "base", "viewState": {"selection": [0, 0], "scroll": [0.0, 0.0]}}],
        "activePath": "README.md",
        "topTabs": {"order": ["Chat", "Changes", document], "activeKey": document, "panes": [{"key": "Changes", "state": {
            "panel_name": "Changes", "children": [], "info": {"panel": {
                "session": "session", "sha": "11c130a1c60f0ed109e4f00be8ad043abe07c40d", "path": "docs/record.md", "title": "11c130a A record",
                "expectedSource": {"machine": "actual-owner", "generation": 1, "routeMachine": "workbox-test"}, "split": false,
            }},
        }}]},
    })
}
fn start(
    cx: &mut TestAppContext,
) -> (
    FakeBackend,
    Arc<Mutex<Vec<String>>>,
    Entity<WorkspaceCoordinator>,
) {
    let methods = Arc::new(Mutex::new(Vec::new()));
    let seen = methods.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" || frame["feature"] != "workspaces" {
            return Vec::new();
        }
        seen.lock()
            .unwrap()
            .push(frame["method"].as_str().unwrap().to_string());
        assert_eq!(frame["method"], "read", "opening a session saves nothing");
        vec![Reply::Frame(
            json!({"t": "result", "id": frame["id"], "result": {"revision": 5, "snapshot": saved()}}),
        )]
    });
    let coordinator = install(&backend, cx);
    (backend, methods, coordinator)
}
fn install(backend: &FakeBackend, cx: &mut TestAppContext) -> Entity<WorkspaceCoordinator> {
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        Preferences::init(cx);
        SessionPane::register(cx);
        ChangesPane::register(cx);
        HistoryPane::register(cx);
        PrPane::register(cx);
        FileView::register(cx);
        WorkspacePanes::register(Box::new(TopTabsCodec), cx).unwrap();
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint.clone(), cx);
        SocketClient::start_bulk(endpoint, cx);
        WorkspaceCoordinator::init(cx)
    })
}
/// The center panes hold the dock weakly, so the test keeps it the way the frame does.
fn add_dock(window: &mut Window, cx: &mut App) -> Entity<DockArea> {
    let dock = cx.new(|cx| DockArea::new("workspace-file-restore", Some(2), window, cx));
    CenterPanes::init(&dock, cx);
    dock.update(cx, |dock, cx| {
        dock.load(default_state(), window, cx).unwrap()
    });
    dock
}
/// A refused open never activates, so the test fails with its reason instead of waiting for an activation.
fn fail_on_refusal(coordinator: &Entity<WorkspaceCoordinator>, cx: &mut App) -> Subscription {
    cx.subscribe(coordinator, |_, event: &WorkspaceRefused, _| {
        panic!("the workspace open was refused: {}", event.reason)
    })
}
fn shown_center_tab(cx: &App) -> String {
    let dock = CenterPanes::dock(cx).unwrap();
    let dock = dock.read(cx);
    let mut shown = String::new();
    dock.layout(DockPlacement::Center)
        .unwrap()
        .root()
        .walk(&mut |node| {
            if let PaneRef::Tabs { panels, active_ix } = node.kind() {
                shown = dock
                    .panel(panels[active_ix])
                    .unwrap()
                    .panel_name(cx)
                    .to_string();
            }
        });
    shown
}
#[gpui::test]
async fn opening_a_session_with_a_saved_active_file_restores_it_and_keeps_the_saved_revision(
    cx: &mut TestAppContext,
) {
    let (_backend, methods, coordinator) = start(cx);
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let _dock = cx.update(add_dock);
    let _refused = cx.update(|_, cx| fail_on_refusal(&coordinator, cx));
    let (sent, mut activated) = unbounded();
    let _heard = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceActivated, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    let target = WorkspaceTarget::Checkout(owner());
    let generation = cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(target.clone(), Box::new(|_, _, _| Ok(())), window, cx)
        })
    });

    let event = activated.recv().await.unwrap();

    assert_eq!(event.generation, generation);
    assert_eq!(event.target, target);
    assert!(event.restored);
    cx.update(|_, cx| {
        let files = CenterPanes::file_views(cx);
        assert_eq!(files.len(), 1);
        assert_eq!(
            files[0].read(cx).restored_workspace_path(&owner()),
            Some("README.md")
        );
        assert_eq!(shown_center_tab(cx), "File");
        assert_eq!(WorkspacePanes::key(cx), Some(&owner()));
        let view = coordinator.read(cx);
        assert_eq!(view.status, None);
        let current = view.current.as_ref().unwrap();
        assert_eq!(current.revision, 5);
        assert!(current.writable);
        assert_eq!(current.safe_to_leave, Some(saved()));
    });
    assert_eq!(*methods.lock().unwrap(), ["read"]);
}
#[gpui::test]
async fn a_transcript_refuses_a_persons_file_but_the_next_checkout_restores_its_saved_file(
    cx: &mut TestAppContext,
) {
    let (_backend, _methods, coordinator) = start(cx);
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let _dock = cx.update(add_dock);
    let _refused = cx.update(|_, cx| fail_on_refusal(&coordinator, cx));
    let (sent, mut activated) = unbounded();
    let _heard = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceActivated, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    let transcript = WorkspaceTarget::TranscriptOnly(crate::core::engine::SessionKey {
        machine: "actual-owner".into(),
        id: "history".into(),
    });
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(transcript.clone(), Box::new(|_, _, _| Ok(())), window, cx);
        })
    });
    assert_eq!(activated.recv().await.unwrap().target, transcript);

    cx.update(|window, cx| {
        FileView::open_at(
            "history".into(),
            "other.rs".into(),
            (None, None),
            true,
            None,
            window,
            cx,
        )
    });

    cx.update(|_, cx| {
        assert!(CenterPanes::file_views(cx).is_empty());
        assert_eq!(
            coordinator.read(cx).status.as_deref(),
            Some("This transcript has no recorded checkout to open files from."),
        );
    });

    let target = WorkspaceTarget::Checkout(owner());
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(target.clone(), Box::new(|_, _, _| Ok(())), window, cx);
        })
    });
    let event = activated.recv().await.unwrap();

    assert_eq!(event.target, target);
    assert!(event.restored);
    cx.update(|_, cx| {
        let files = CenterPanes::file_views(cx);
        assert_eq!(files.len(), 1);
        assert_eq!(
            files[0].read(cx).restored_workspace_path(&owner()),
            Some("README.md")
        );
        assert_eq!(shown_center_tab(cx), "File");
        assert_eq!(coordinator.read(cx).status, None);
    });
}

const NO_IDENTITY: &str = "Not saved: A file tab has no confirmed owning workspace identity.";
fn elsewhere() -> WorkspaceKey {
    WorkspaceKey {
        machine: "actual-owner".into(),
        session_id: "session".into(),
        checkout_root: "/elsewhere".into(),
    }
}
/// The same saved session with the file behind Chat, which is how a person leaves it after going back to the chat.
fn saved_behind_chat() -> Value {
    let mut saved = saved();
    saved.as_object_mut().unwrap().remove("activePath");
    saved["topTabs"]["activeKey"] = json!("Chat");
    saved
}
#[derive(Clone, Default)]
struct Heard {
    /// Each call as `feature/method`.
    calls: Arc<Mutex<Vec<String>>>,
    saves: Arc<Mutex<Vec<Value>>>,
}
impl Heard {
    fn count(&self, call: &str) -> usize {
        self.calls
            .lock()
            .unwrap()
            .iter()
            .filter(|heard| *heard == call)
            .count()
    }
}
/// Answers reads with `snapshot` for the owner's checkout and nothing saved anywhere else, or everywhere when it's null;
/// takes every save, and says the file belongs to `owner_root`. With no owner root, the question goes unanswered.
fn start_answering(
    snapshot: Value,
    owner_root: Option<&'static str>,
    cx: &mut TestAppContext,
) -> (FakeBackend, Heard, Entity<WorkspaceCoordinator>) {
    let heard = Heard::default();
    let told = heard.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return Vec::new();
        }
        let call = format!(
            "{}/{}",
            frame["feature"].as_str().unwrap(),
            frame["method"].as_str().unwrap()
        );
        told.calls.lock().unwrap().push(call.clone());
        let result = match call.as_str() {
            "workspaces/read"
                if frame["args"]["checkoutRoot"] == "/checkout" && !snapshot.is_null() =>
            {
                json!({"revision": 5, "snapshot": snapshot})
            }
            "workspaces/read" => json!({"revision": 0, "snapshot": null}),
            "workspaces/save" => {
                told.saves
                    .lock()
                    .unwrap()
                    .push(frame["args"]["snapshot"].clone());
                json!({"revision": frame["args"]["expectedRevision"].as_u64().unwrap() + 1})
            }
            "files/owner" => match owner_root {
                Some(root) => {
                    json!({"machine": "actual-owner", "checkoutRoot": root, "remoteSessionId": "session"})
                }
                None => return Vec::new(),
            },
            _ => return Vec::new(),
        };
        vec![Reply::Frame(
            json!({"t": "result", "id": frame["id"], "result": result}),
        )]
    });
    let coordinator = install(&backend, cx);
    (backend, heard, coordinator)
}
fn open_workspace(
    key: WorkspaceKey,
    coordinator: &Entity<WorkspaceCoordinator>,
    cx: &mut gpui::VisualTestContext,
) {
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                WorkspaceTarget::Checkout(key),
                Box::new(|_, _, _| Ok(())),
                window,
                cx,
            );
        })
    });
}
/// Opens a file the way a person does, and shows it, which is what asks who owns it.
fn open_as_a_person(cx: &mut gpui::VisualTestContext) -> Entity<FileView> {
    cx.update(|window, cx| {
        FileView::open_at(
            "session".into(),
            "other.rs".into(),
            (None, None),
            true,
            None,
            window,
            cx,
        );
    });
    cx.update(|window, cx| {
        let file = CenterPanes::file_views(cx)[0].clone();
        file.update(cx, |file, cx| BasePanel::set_active(file, true, window, cx));
        file
    })
}
// Pins: a tab restored behind Chat isn't shown, so its owner isn't read, and that used to refuse every save and every
// departure from the session, which the rail showed as a red line and no way out.
#[gpui::test]
async fn a_file_restored_behind_chat_saves_under_its_checkout_and_lets_the_session_be_left(
    cx: &mut TestAppContext,
) {
    let (_backend, heard, coordinator) = start_answering(saved_behind_chat(), None, cx);
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let _dock = cx.update(add_dock);
    let _refused = cx.update(|_, cx| fail_on_refusal(&coordinator, cx));
    let (sent, mut activated) = unbounded();
    let _heard = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceActivated, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    open_workspace(owner(), &coordinator, cx);
    activated.recv().await.unwrap();
    let asked = heard.count("files/owner");

    let captured = cx
        .update(|window, cx| WorkspacePanes::capture(window, cx))
        .unwrap();

    let document = r#"document:["actual-owner","/checkout","README.md"]"#;
    assert_eq!(captured["files"][0]["path"], "README.md");
    assert_eq!(
        captured["activePath"],
        Value::Null,
        "the file stays behind Chat"
    );
    assert!(
        captured["topTabs"]["order"]
            .as_array()
            .unwrap()
            .iter()
            .any(|key| key == document)
    );
    assert_eq!(captured["topTabs"]["activeKey"], "Chat");
    cx.update(|_, cx| {
        let file = CenterPanes::file_views(cx)[0].read(cx);
        assert_eq!(file.workspace_path(&owner()).as_deref(), Ok("README.md"));
        assert!(file.workspace_path(&elsewhere()).is_err());
    });

    let next = WorkspaceKey {
        session_id: "next".into(),
        checkout_root: "/next".into(),
        ..owner()
    };
    open_workspace(next.clone(), &coordinator, cx);
    let event = activated.recv().await.unwrap();

    assert_eq!(event.target, WorkspaceTarget::Checkout(next));
    assert_eq!(cx.update(|_, cx| coordinator.read(cx).status.clone()), None);
    assert_eq!(
        heard.count("files/owner"),
        asked,
        "saving and leaving read no hidden file's owner"
    );
}
// Pins: a file a person just opened can't be saved until its owner is known, and nothing else was going to save it
// when the answer came, so the red line stayed until some later change.
#[gpui::test]
async fn a_file_a_person_opened_is_refused_until_its_owner_answers_and_then_saves_itself(
    cx: &mut TestAppContext,
) {
    let (_backend, heard, coordinator) = start_answering(Value::Null, Some("/checkout"), cx);
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let _dock = cx.update(add_dock);
    let _refused = cx.update(|_, cx| fail_on_refusal(&coordinator, cx));
    let (sent, mut activated) = unbounded();
    let _heard = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceActivated, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    open_workspace(owner(), &coordinator, cx);
    activated.recv().await.unwrap();
    let (changed, mut changes) = unbounded();
    let told = changed.clone();
    let _saved = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, _: &WorkspaceSaved, _| {
            told.unbounded_send(()).unwrap();
        })
    });

    cx.update(|window, cx| {
        FileView::open_at(
            "session".into(),
            "other.rs".into(),
            (None, None),
            true,
            None,
            window,
            cx,
        );
    });
    assert_eq!(
        cx.update(|_, cx| coordinator.read(cx).status.clone())
            .as_deref(),
        Some(NO_IDENTITY)
    );
    let file = cx.update(|_, cx| CenterPanes::file_views(cx)[0].clone());
    cx.update(|window, cx| {
        file.update(cx, |file, cx| BasePanel::set_active(file, true, window, cx))
    });

    let saves = heard.saves.clone();
    until("the opened file saved", &mut changes, &changed, || {
        !saves.lock().unwrap().is_empty()
    })
    .await;

    let document = r#"document:["actual-owner","/checkout","other.rs"]"#;
    let saved = heard.saves.lock().unwrap()[0].clone();
    assert!(
        saved["topTabs"]["order"]
            .as_array()
            .unwrap()
            .iter()
            .any(|key| key == document)
    );
    cx.run_until_parked();
    assert_eq!(cx.update(|_, cx| coordinator.read(cx).status.clone()), None);
}
// Pins: knowing the owner isn't the same as belonging here. A file owned by another checkout stays refused.
#[gpui::test]
async fn a_file_a_person_opened_that_another_checkout_owns_stays_refused_and_saves_nothing(
    cx: &mut TestAppContext,
) {
    let (_backend, heard, coordinator) = start_answering(Value::Null, Some("/elsewhere"), cx);
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let _dock = cx.update(add_dock);
    let _refused = cx.update(|_, cx| fail_on_refusal(&coordinator, cx));
    let (sent, mut activated) = unbounded();
    let _heard = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceActivated, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    open_workspace(owner(), &coordinator, cx);
    activated.recv().await.unwrap();
    let file = open_as_a_person(cx);
    let (changed, mut changes) = unbounded();
    let told = changed.clone();
    let _notified = cx.update(|_, cx| {
        cx.observe(&file, move |_, _| {
            told.unbounded_send(()).unwrap();
        })
    });

    until("the owner answered", &mut changes, &changed, || {
        cx.update(|_, cx| {
            file.read(cx)
                .restored_workspace_path(&elsewhere())
                .is_some()
        })
    })
    .await;
    cx.run_until_parked();

    cx.update(|_, cx| {
        assert_eq!(coordinator.read(cx).status.as_deref(), Some(NO_IDENTITY));
        assert!(file.read(cx).workspace_path(&owner()).is_err());
    });
    assert!(heard.saves.lock().unwrap().is_empty());
}
// Pins: an owner answer that contradicts the saved checkout reads nothing and doesn't move the tab to that checkout.
#[gpui::test]
async fn a_restored_file_answered_by_another_checkout_reads_nothing_and_keeps_its_saved_place(
    cx: &mut TestAppContext,
) {
    let (_backend, heard, coordinator) = start_answering(saved(), Some("/elsewhere"), cx);
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let _dock = cx.update(add_dock);
    let _refused = cx.update(|_, cx| fail_on_refusal(&coordinator, cx));
    let (sent, mut activated) = unbounded();
    let _heard = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceActivated, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    open_workspace(owner(), &coordinator, cx);
    activated.recv().await.unwrap();
    let file = cx.update(|window, cx| {
        let file = CenterPanes::file_views(cx)[0].clone();
        file.update(cx, |file, cx| {
            BasePanel::set_active(file, false, window, cx);
            BasePanel::set_active(file, true, window, cx);
        });
        file
    });
    let (changed, mut changes) = unbounded();
    let told = changed.clone();
    let answered = Arc::new(Mutex::new(false));
    let noticed = answered.clone();
    let _notified = cx.update(|_, cx| {
        cx.observe(&file, move |_, _| {
            *noticed.lock().unwrap() = true;
            told.unbounded_send(()).unwrap();
        })
    });

    until("the owner answered", &mut changes, &changed, || {
        *answered.lock().unwrap()
    })
    .await;
    cx.run_until_parked();

    cx.update(|_, cx| {
        let view = file.read(cx);
        assert_eq!(view.workspace_path(&owner()).as_deref(), Ok("README.md"));
        assert!(view.workspace_path(&elsewhere()).is_err());
    });
    assert_eq!(
        heard
            .calls
            .lock()
            .unwrap()
            .iter()
            .filter(|call| call.starts_with("files/") && *call != "files/owner")
            .count(),
        0,
        "nothing was read from the other checkout"
    );
}
#[path = "workspace_view_file_tests.rs"]
mod view_file;
