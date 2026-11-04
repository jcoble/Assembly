use super::{
    SocketClient, WorkspaceActivated, WorkspaceCoordinator, WorkspaceKey, WorkspaceSaved,
    WorkspaceTarget,
};
use crate::core::{
    socket::{
        endpoint::Endpoint,
        fake_backend::{FakeBackend, Reply, hello},
        test_wait::until,
    },
    ui::center_panes::CenterPanes,
};
use crate::features::files::{
    document_store::{DocumentKey, DocumentStore},
    file_view::FileView,
};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::component::dock::{DockArea, DockAreaState, PanelState};
use gpui_kit::{AppContext as _, Context, IntoElement, Render, Window, div};
use serde_json::json;
struct Host;
impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
fn files(panel: &PanelState) -> usize {
    usize::from(panel.panel_name == "File") + panel.children.iter().map(files).sum::<usize>()
}
#[gpui::test]
async fn global_file_metadata_is_pruned_only_after_the_exact_real_workspace_cas_and_newer_drafts_stop_pruning(
    cx: &mut TestAppContext,
) {
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return Vec::new();
        }
        let result = match (frame["feature"].as_str(), frame["method"].as_str()) {
            (Some("workspaces"), Some("read")) => json!({"revision":0,"snapshot":null}),
            (Some("workspaces"), Some("save")) => {
                assert_eq!(frame["args"]["expectedRevision"], 0);
                assert_eq!(frame["args"]["snapshot"]["files"][0]["path"], "file.rs");
                json!({"revision":1})
            }
            (Some("files"), Some("owner")) => {
                json!({"machine":"actual-owner","remoteSessionId":"session","checkoutRoot":"/checkout"})
            }
            (Some("files"), Some("read")) => {
                json!({"content":"disk","revision":"base","size":4,"lineCount":1,"binary":false,"nextOffset":null})
            }
            (Some("files"), Some("release")) => json!({}),
            _ => panic!("unexpected IO {frame}"),
        };
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
    cx.executor().allow_parking();
    let coordinator = cx.update(|cx| {
        gpui_kit::init(cx);
        FileView::register(cx);
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
        let dock = cx.new(|cx| DockArea::new("workspace-layout", Some(2), window, cx));
        CenterPanes::init(&dock, cx);
        dock
    });
    let (sent, mut activated) = unbounded();
    let _activated = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceActivated, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    let key = WorkspaceKey {
        machine: "actual-owner".into(),
        session_id: "session".into(),
        checkout_root: "/checkout".into(),
    };
    cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(
                WorkspaceTarget::Checkout(key.clone()),
                Box::new(|_, _, _| Ok(())),
                window,
                cx,
            );
        })
    });
    activated.recv().await.unwrap();
    cx.update(|window, cx| FileView::restore_workspace(&key, "file.rs".into(), None, window, cx));
    let file = cx.update(|_, cx| CenterPanes::file_views(cx).pop().unwrap());
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _observe = cx.update(|_, cx| {
        cx.observe(&file, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    until(
        "the actual owning file identity",
        &mut changes,
        &changed,
        || {
            file.read_with(cx, |file, cx| {
                file.legacy_capture(cx).unwrap().owner.is_some()
            })
        },
    )
    .await;
    let original: DockAreaState = cx.update(|_, cx| dock.read(cx).dump(cx));
    assert_eq!(files(&original.center), 1);
    cx.update(|window, cx| {
        assert!(
            coordinator
                .read(cx)
                .layout_to_save(&original, window, cx)
                .unwrap()
                .is_none()
        )
    });
    let (sent, mut saved) = unbounded();
    let _saved = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceSaved, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    cx.update(|_, cx| WorkspaceCoordinator::changed(cx));
    assert_eq!(saved.recv().await.unwrap().revision, 1);
    cx.update(|window, cx| {
        let projected = coordinator
            .read(cx)
            .layout_to_save(&original, window, cx)
            .unwrap()
            .unwrap();
        assert_eq!(files(&projected.center), 0);
        assert_eq!(original.version, projected.version);
        assert_eq!(original.left_dock, projected.left_dock);
        assert_eq!(files(&original.center), 1);
        let document = DocumentKey {
            machine: key.machine.clone(),
            checkout_root: key.checkout_root.clone(),
            path: "file.rs".into(),
        };
        if !cx.has_global::<DocumentStore>() {
            cx.set_global(DocumentStore::default());
        }
        cx.global_mut::<DocumentStore>()
            .open(document.clone(), "base".into(), "disk".into())
            .unwrap();
        cx.global_mut::<DocumentStore>()
            .replace(&document, 0, "newer actual draft".into())
            .unwrap();
        assert!(
            coordinator
                .read(cx)
                .layout_to_save(&original, window, cx)
                .unwrap()
                .is_none()
        );
        assert_eq!(
            cx.global::<DocumentStore>().draft(&document),
            Some("newer actual draft")
        );
    });
}
