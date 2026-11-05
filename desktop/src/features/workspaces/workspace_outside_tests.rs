//! Feature 28, ruling 8: an outside file belongs to the workspace that was open when it opened. It's saved and
//! restored with that workspace, locked; opened with no workspace, it isn't saved and closes when a session opens.
use super::{
    SocketClient, WorkspaceActivated, WorkspaceCoordinator, WorkspaceKey, WorkspaceRefused,
    WorkspaceSaved, WorkspaceTarget,
};
use crate::core::intents::open_file::{OpenFile, OutsideFile};
use crate::core::socket::{
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use crate::core::state::{selected_session::SelectedSession, workspace_panes::WorkspacePanes};
use crate::core::ui::center_panes::CenterPanes;
use crate::features::files::file_view::FileView;
use crate::features::workspaces::top_tabs_codec::TopTabsCodec;
use futures_channel::mpsc::unbounded;
use gpui::{TestAppContext, VisualTestContext};
use gpui_kit::component::dock::{DockArea, DockAreaState, PanelState};
use gpui_kit::{AppContext as _, Context, Entity, IntoElement, Render, Subscription, Window, div};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

struct Host;
impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
fn files(panel: &PanelState) -> usize {
    usize::from(panel.panel_name == "File") + panel.children.iter().map(files).sum::<usize>()
}
fn key() -> WorkspaceKey {
    WorkspaceKey {
        machine: "actual-owner".into(),
        session_id: "session".into(),
        checkout_root: "/checkout".into(),
    }
}
const HOSTS: &str = r#"document:["engine-1","/etc","hosts"]"#;
/// Every workspace snapshot saved, in order.
type Saves = Arc<Mutex<Vec<Value>>>;

struct Stage<'a> {
    cx: &'a mut VisualTestContext,
    coordinator: Entity<WorkspaceCoordinator>,
    saves: Saves,
    dock: Entity<DockArea>,
    _refused: Subscription,
    _backend: FakeBackend,
}

fn stage(cx: &mut TestAppContext) -> Stage<'_> {
    let saves = Saves::default();
    let saved = saves.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return Vec::new();
        }
        let args = &frame["args"];
        let result = match (frame["feature"].as_str(), frame["method"].as_str()) {
            (Some("workspaces"), Some("read")) => json!({"revision": 0, "snapshot": null}),
            (Some("workspaces"), Some("save")) => {
                saved.lock().unwrap().push(args["snapshot"].clone());
                json!({"revision": args["expectedRevision"].as_u64().unwrap() + 1})
            }
            (Some("files"), Some("owner")) => {
                assert!(
                    args.get("session").is_none(),
                    "an outside file asks by target"
                );
                json!({"machine": "engine-1", "checkoutRoot": "/etc", "remoteSessionId": "", "file": "hosts"})
            }
            (Some("files"), Some("read")) => {
                json!({"content": "disk\n", "revision": "r1", "size": 5, "lineCount": 1, "binary": false, "nextOffset": null})
            }
            (Some("files"), Some("release")) => json!({}),
            _ => panic!("unexpected call {frame}"),
        };
        vec![Reply::Frame(
            json!({"t": "result", "id": frame["id"], "result": result}),
        )]
    });
    cx.executor().allow_parking();
    let coordinator = cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
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
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let dock = cx.update(|window, cx| {
        let dock = cx.new(|cx| DockArea::new("workspace-outside", Some(2), window, cx));
        CenterPanes::init(&dock, cx);
        window.activate_window();
        dock
    });
    let _refused = cx.update(|_, cx| {
        cx.subscribe(&coordinator, |_, event: &WorkspaceRefused, _| {
            panic!("the workspace open was refused: {}", event.reason)
        })
    });
    Stage {
        cx,
        coordinator,
        saves,
        dock,
        _refused,
        _backend: backend,
    }
}

impl Stage<'_> {
    async fn open_workspace(&mut self) {
        let (sent, mut activated) = unbounded();
        let _heard = self.cx.update(|_, cx| {
            cx.subscribe(
                &self.coordinator,
                move |_, event: &WorkspaceActivated, _| {
                    sent.unbounded_send(event.clone()).unwrap();
                },
            )
        });
        let coordinator = self.coordinator.clone();
        self.cx.update(|window, cx| {
            coordinator.update(cx, |view, cx| {
                let target = WorkspaceTarget::Checkout(key());
                view.request(target, Box::new(|_, _, _| Ok(())), window, cx)
            })
        });
        activated.recv().await.unwrap();
    }

    /// Opens /etc/hosts as an outside file and waits until it's read and in its editor.
    async fn open_hosts(&mut self) -> Entity<FileView> {
        let intent = OpenFile {
            session_id: String::new(),
            path: "/etc/hosts".into(),
            expected_source: None,
            line: None,
            column: None,
            pinned: true,
            outside: Some(OutsideFile {
                machine: None,
                path: "/etc/hosts".into(),
            }),
        };
        self.cx
            .update(|_, cx| cx.defer(move |cx| cx.dispatch_action(&intent)));
        self.cx.run_until_parked();
        let view = self
            .cx
            .update(|_, cx| CenterPanes::file_views(cx).pop())
            .unwrap();
        let (changed, mut changes) = unbounded();
        let tell = changed.clone();
        let _heard = self.cx.update(|_, cx| {
            cx.observe(&view, move |_, _| {
                let _ = tell.unbounded_send(());
            })
        });
        until(
            "the outside file in its editor",
            &mut changes,
            &changed,
            || {
                self.cx
                    .update(|window, cx| view.update(cx, |view, cx| view.bind_for_test(window, cx)))
            },
        )
        .await;
        self.cx.run_until_parked();
        view
    }

    fn layout(&mut self) -> DockAreaState {
        let dock = self.dock.clone();
        self.cx.update(|_, cx| dock.read(cx).dump(cx))
    }
}

#[gpui::test]
async fn an_outside_tab_is_captured_with_its_workspace_and_restored_locked(
    cx: &mut TestAppContext,
) {
    let mut stage = stage(cx);
    stage.open_workspace().await;
    let (sent, mut saved) = unbounded();
    let _saved = stage.cx.update(|_, cx| {
        cx.subscribe(&stage.coordinator, move |_, event: &WorkspaceSaved, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    stage.open_hosts().await;
    // Each save is of the live view, so the last one holds the outside file with its engine.
    for round in 0.. {
        assert!(round < 10, "no save held the outside file");
        saved.recv().await.unwrap();
        let last = stage.saves.lock().unwrap().last().cloned().unwrap();
        let idle = stage
            .cx
            .update(|_, cx| stage.coordinator.read(cx).saving.is_none());
        if idle && last["topTabs"]["order"] == json!([HOSTS]) {
            break;
        }
    }
    let snapshot = stage.saves.lock().unwrap().last().cloned().unwrap();
    let entry = &snapshot["files"][0];
    assert_eq!(entry["path"], "/etc/hosts");
    assert_eq!(
        entry["outside"],
        json!({"machine": null, "engine": "engine-1"})
    );
    assert_eq!(snapshot["activePath"], "/etc/hosts");
    assert_eq!(snapshot["topTabs"]["activeKey"], HOSTS);

    // Its pane is saved with the workspace, not in the dock's layout.
    let layout = stage.layout();
    assert_eq!(files(&layout.center), 1);
    let coordinator = stage.coordinator.clone();
    let projected = stage.cx.update(|window, cx| {
        coordinator
            .read(cx)
            .layout_to_save(&layout, window, cx)
            .unwrap()
            .expect("a layout to save")
    });
    assert_eq!(files(&projected.center), 0);

    stage.cx.update(|window, cx| {
        WorkspacePanes::deactivate(window, cx);
        assert!(
            CenterPanes::file_views(cx).is_empty(),
            "closed with its workspace"
        );
        WorkspacePanes::restore(&snapshot, window, cx).unwrap();
    });
    stage.cx.run_until_parked();
    stage.cx.update(|_, cx| {
        let views = CenterPanes::file_views(cx);
        assert_eq!(views.len(), 1);
        let view = views[0].read(cx);
        assert!(view.is_outside());
        assert!(view.is_locked(), "a restored outside tab comes back locked");
        assert_eq!(view.restored_identity(&key()).as_deref(), Some(HOSTS));
    });
}

#[gpui::test]
async fn an_outside_tab_with_no_workspace_is_not_saved(cx: &mut TestAppContext) {
    let mut stage = stage(cx);
    stage.open_hosts().await;
    let layout = stage.layout();
    assert_eq!(files(&layout.center), 1);
    let coordinator = stage.coordinator.clone();
    let projected = stage.cx.update(|window, cx| {
        coordinator
            .read(cx)
            .layout_to_save(&layout, window, cx)
            .unwrap()
            .expect("a layout to save")
    });
    assert_eq!(files(&projected.center), 0, "not saved by the back door");

    // The stated limit: opening a session's workspace closes it, and nothing saves it.
    stage.open_workspace().await;
    stage.cx.run_until_parked();
    stage
        .cx
        .update(|_, cx| assert!(CenterPanes::file_views(cx).is_empty()));
    assert!(stage.saves.lock().unwrap().iter().all(|snapshot| {
        snapshot
            .get("files")
            .is_none_or(|files| files == &json!([]))
    }));
}
