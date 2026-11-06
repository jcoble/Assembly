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
use crate::features::files::document_store::{DocumentKey, DocumentStore};
use crate::features::files::file_view::{FileView, saved_alias};
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
/// The document an outside /etc/hosts opens as.
pub(super) fn hosts_document() -> DocumentKey {
    DocumentKey {
        machine: "engine-1".into(),
        checkout_root: "/etc".into(),
        path: "hosts".into(),
    }
}
const README: &str = "/checkout/README.md";
/// Whether a tab is ready to look at, which a test waits for.
type Ready = fn(&mut FileView, &mut Window, &mut Context<FileView>) -> bool;
/// Ready once its text is in its editor.
fn in_editor(view: &mut FileView, window: &mut Window, cx: &mut Context<FileView>) -> bool {
    view.bind_for_test(window, cx)
}
/// Every workspace snapshot saved, in order.
type Saves = Arc<Mutex<Vec<Value>>>;

pub(super) struct Stage<'a> {
    pub(super) cx: &'a mut VisualTestContext,
    pub(super) coordinator: Entity<WorkspaceCoordinator>,
    saves: Saves,
    dock: Entity<DockArea>,
    pub(super) _refused: Subscription,
    _backend: FakeBackend,
}

pub(super) fn stage(cx: &mut TestAppContext) -> Stage<'_> {
    let saves = Saves::default();
    let saved = saves.clone();
    // A file save's commit, held until the test's release call, which may come first.
    let mut held: Option<Value> = None;
    let mut released = false;
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
            (Some("files"), Some("owner")) if args.get("session").is_some() => {
                json!({"machine": "actual-owner", "checkoutRoot": "/checkout", "remoteSessionId": "session"})
            }
            // The session's README asked by path: its owner answers, then its read fails.
            (Some("files"), Some("owner")) if args["target"]["path"] == README => {
                json!({"machine": "actual-owner", "checkoutRoot": "/checkout", "remoteSessionId": "", "file": "README.md"})
            }
            (Some("files"), Some("owner")) => {
                json!({"machine": "engine-1", "checkoutRoot": "/etc", "remoteSessionId": "", "file": "hosts"})
            }
            (Some("files"), Some("read")) if args["target"]["path"] == README => {
                return vec![Reply::Frame(
                    json!({"t": "error", "id": frame["id"], "code": "failed", "message": "that file isn't there"}),
                )];
            }
            (Some("files"), Some("read")) => {
                json!({"content": "disk\n", "revision": "r1", "size": 5, "lineCount": 1, "binary": false, "nextOffset": null})
            }
            (Some("files"), Some("release")) => json!({}),
            (Some("files"), Some("beginSave")) => json!({"upload": "u1"}),
            (Some("files"), Some("saveChunk" | "dropSave")) => json!({}),
            (Some("files"), Some("commitSave")) if released => {
                json!({"state": "saved", "revision": "r2"})
            }
            (Some("files"), Some("commitSave")) => {
                held = Some(
                    json!({"t": "result", "id": frame["id"], "result": {"state": "saved", "revision": "r2"}}),
                );
                return Vec::new();
            }
            (Some("test"), Some("release")) => {
                released = true;
                let done = json!({"t": "result", "id": frame["id"], "result": {}});
                return held
                    .take()
                    .into_iter()
                    .chain([done])
                    .map(Reply::Frame)
                    .collect();
            }
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
    pub(super) async fn open_hosts(&mut self, machine: Option<&str>) -> Entity<FileView> {
        let file = OutsideFile {
            machine: machine.map(str::to_owned),
            path: "/etc/hosts".into(),
        };
        self.open("", "/etc/hosts", Some(file), in_editor).await
    }

    /// Opens a file as a link does and waits until `ready` says it is.
    async fn open(
        &mut self,
        session: &str,
        path: &str,
        outside: Option<OutsideFile>,
        ready: Ready,
    ) -> Entity<FileView> {
        let intent = OpenFile {
            session_id: session.into(),
            path: path.into(),
            expected_source: None,
            line: None,
            column: None,
            pinned: true,
            outside,
        };
        let alias = match &intent.outside {
            Some(file) => {
                let fields = json!({"outside": true, "machine": file.machine, "path": file.path});
                saved_alias(fields.as_object().unwrap()).unwrap()
            }
            None => FileView::session_tab_key(session, path),
        };
        self.cx
            .update(|_, cx| cx.defer(move |cx| cx.dispatch_action(&intent)));
        self.cx.run_until_parked();
        let view = self
            .cx
            .update(|_, cx| CenterPanes::find::<FileView>(&alias, cx))
            .expect("the file's tab");

        let (changed, mut changes) = unbounded();
        let tell = changed.clone();
        let _heard = self.cx.update(|_, cx| {
            cx.observe(&view, move |_, _| {
                let _ = tell.unbounded_send(());
            })
        });
        until("the file ready to look at", &mut changes, &changed, || {
            self.cx
                .update(|window, cx| view.update(cx, |view, cx| ready(view, window, cx)))
        })
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
    stage.open_hosts(Some("box")).await;
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
        json!({"machine": "box", "engine": "engine-1"})
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

    // A draft taken with the workspace comes back with it, though this app's drafts are gone.
    let document = DocumentKey {
        machine: "engine-1".into(),
        checkout_root: "/etc".into(),
        path: "hosts".into(),
    };
    stage.cx.update(|window, cx| {
        let docs = cx.global_mut::<DocumentStore>();
        let version = docs.snapshot(&document).unwrap().version;
        docs.replace(&document, version, "edited\n".into()).unwrap();
        let captured = WorkspacePanes::capture(window, cx).unwrap();
        WorkspacePanes::deactivate(window, cx);
        assert!(
            CenterPanes::file_views(cx).is_empty(),
            "closed with its workspace"
        );
        cx.set_global(DocumentStore::default());
        WorkspacePanes::restore(&captured, window, cx).unwrap();
    });
    stage.cx.run_until_parked();
    stage.cx.update(|_, cx| {
        let views = CenterPanes::file_views(cx);
        assert_eq!(views.len(), 1);
        let view = views[0].read(cx);
        assert!(view.is_outside());
        assert!(view.is_locked(), "a restored outside tab comes back locked");
        assert_eq!(
            view.outside_file(),
            Some(&OutsideFile {
                machine: Some("box".into()),
                path: "/etc/hosts".into(),
            })
        );
        assert_eq!(view.restored_identity(&key()).as_deref(), Some(HOSTS));
        assert_eq!(view.document_key(), Some(&document));
        let docs = cx.global::<DocumentStore>();
        assert_eq!(docs.draft(&document), Some("edited\n"));
        assert_eq!(docs.base_revision(&document), Some("r1"));
    });
}

#[gpui::test]
async fn an_outside_tab_with_no_workspace_is_not_saved(cx: &mut TestAppContext) {
    let mut stage = stage(cx);
    stage.open_hosts(None).await;
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

// Ruling 19: a session file and an outside tab on its document make one tab. Until the outside tab is claimed (its
// read failed here, so it never is), the first tab stands for the document and the workspace still saves.
#[gpui::test]
async fn a_session_file_and_an_unclaimed_outside_tab_on_it_save_as_one(cx: &mut TestAppContext) {
    let mut stage = stage(cx);
    stage.open_workspace().await;
    stage.open("session", "README.md", None, in_editor).await;
    let failed: Ready = |view, _, _| view.text_error().is_some();
    let file = OutsideFile {
        machine: None,
        path: README.into(),
    };
    let outside = stage.open("", README, Some(file), failed).await;
    let identity = outside.read_with(stage.cx, |view, _| view.restored_identity(&key()));
    assert_eq!(
        identity.as_deref(),
        Some(r#"document:["actual-owner","/checkout","README.md"]"#)
    );

    let captured = stage
        .cx
        .update(|window, cx| WorkspacePanes::capture(window, cx))
        .expect("the workspace captures");
    assert_eq!(captured["files"].as_array().unwrap().len(), 1);
    assert_eq!(captured["files"][0]["path"], "README.md");
    assert_eq!(captured["topTabs"]["order"], json!([identity.unwrap()]));
}

// Proof fixes brief item 3: a layout save refused while an outside file saved goes through once the save ends, so the
// "Not saved" line doesn't stay.
#[gpui::test]
async fn a_layout_save_refused_during_an_outside_save_goes_through_when_it_ends(
    cx: &mut TestAppContext,
) {
    let mut stage = stage(cx);
    stage.open_workspace().await;
    let view = stage.open_hosts(Some("box")).await;
    let coordinator = stage.coordinator.clone();
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _heard = stage.cx.update(|_, cx| {
        cx.observe(&coordinator, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    let idle = |stage: &mut Stage<'_>| {
        stage
            .cx
            .update(|_, cx| stage.coordinator.read(cx).saving.is_none())
    };
    until("the open saved", &mut changes, &changed, || {
        stage.cx.run_until_parked();
        idle(&mut stage)
    })
    .await;

    let (answer, _saved) = futures_channel::oneshot::channel();
    stage.cx.update(|_, cx| {
        view.update(cx, |view, cx| view.edit_anyway(cx));
        let docs = cx.global_mut::<DocumentStore>();
        let version = docs.snapshot(&hosts_document()).unwrap().version;
        docs.replace(&hosts_document(), version, "edited\n".into())
            .unwrap();
        view.update(cx, |view, cx| view.save_for_guard(answer, cx));
    });
    // A view change while the file saves: its layout save is refused for now.
    stage.cx.update(|_, cx| WorkspaceCoordinator::changed(cx));
    stage.cx.run_until_parked();
    let status = stage.cx.update(|_, cx| coordinator.read(cx).status.clone());
    assert!(
        status.is_some_and(|status| status.starts_with("Not saved:")),
        "refused while the file saves"
    );

    let release = stage.cx.update(|_, cx| {
        // The file save goes over the bulk connection, so its held answer comes back on it.
        SocketClient::try_bulk(cx).unwrap().read(cx).call::<Value>(
            "test",
            "release",
            json!({}),
            None,
        )
    });
    until(
        "the layout saved after the file",
        &mut changes,
        &changed,
        || {
            stage.cx.run_until_parked();
            let status = stage.cx.update(|_, cx| coordinator.read(cx).status.clone());
            status.is_none() && idle(&mut stage)
        },
    )
    .await;
    drop(release);
    let last = stage.saves.lock().unwrap().last().cloned().unwrap();
    assert_eq!(last["files"][0]["path"], "/etc/hosts");
    assert!(
        last["files"][0]["draft"].is_null(),
        "the saved file has no draft left: {last}"
    );
}
