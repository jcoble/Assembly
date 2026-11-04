//! Opening a session whose saved workspace has an active file, through the real file and center tab codecs.
use super::{
    SocketClient, WorkspaceActivated, WorkspaceCoordinator, WorkspaceKey, WorkspaceTarget,
};
use crate::core::socket::{
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
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
use gpui_kit::component::dock::{DockArea, DockPlacement, PaneRef};
use gpui_kit::{App, AppContext as _, Context, Entity, IntoElement, Render, Window, div};
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
    cx.executor().allow_parking();
    let coordinator = cx.update(|cx| {
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
    });
    (backend, methods, coordinator)
}
fn add_dock(window: &mut Window, cx: &mut App) {
    let dock = cx.new(|cx| DockArea::new("workspace-file-restore", Some(2), window, cx));
    CenterPanes::init(&dock, cx);
    dock.update(cx, |dock, cx| {
        dock.load(default_state(), window, cx).unwrap()
    });
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
    cx.update(add_dock);
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
    cx.update(add_dock);
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
            None,
            None,
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
