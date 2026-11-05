//! The panel says why instead of going blank: no session, no folder, a folder not made, or a workspace not committed.
use super::file_view::FileView;
use super::files_pane::FilesPane;
use super::files_real_tests::{selected_session, settled, shape};
use super::files_reload_real_tests::shown_pane;
use super::panel_message::PanelMessage;
use super::tree_codec::TreeSnapshot;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::state::folder_touched::FolderTouched;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::Sessions;
use crate::core::state::workspace_panes::{WorkspaceKey, WorkspacePanes};
use crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator;
use gpui::TestAppContext;
use gpui_kit::component::dock::BasePanel;
use serde_json::{Value, json};
use std::fs;
use std::path::Path;
use std::time::Duration;

fn selected_id(cx: &mut TestAppContext) -> String {
    cx.read(|cx| {
        SelectedSession::global(cx)
            .read(cx)
            .session_id()
            .unwrap()
            .to_string()
    })
}

#[gpui::test]
async fn a_session_with_no_folder_keeps_saying_so_after_any_notify(cx: &mut TestAppContext) {
    // The real backend can't make a session with no folder: only a stored id that can't name one has none.
    let backend = FakeBackend::start(|frame: &Value| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] == "sub" {
            return vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[]}),
            )];
        }
        if frame["t"] != "call" {
            return vec![];
        }
        vec![Reply::Frame(json!({
            "t":"error", "id":frame["id"], "code":"no_folder", "message":"this session has no folder"
        }))]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
        SelectedSession::global(cx).update(cx, |selected, cx| {
            selected.select(Some("no-folder".into()), cx)
        });
    });
    let (pane, cx) = cx.add_window_view(|_, cx| FilesPane::new(cx));
    pane.update_in(cx, |pane, window, cx| {
        BasePanel::set_active(pane, true, window, cx)
    });
    settled(&pane, cx, |pane| {
        pane.message() == Some(PanelMessage::NoFolder)
    })
    .await;
    pane.update(cx, |_, cx| cx.notify());
    cx.update(|_, cx| FolderTouched::touch("no-folder", cx));
    settled(&pane, cx, |pane| !pane.is_loading()).await;
    pane.read_with(cx, |pane, _| {
        assert_eq!(pane.message(), Some(PanelMessage::NoFolder));
    });
}

#[gpui::test]
async fn waiting_for_the_workspace_key_shows_waiting_not_blank(cx: &mut TestAppContext) {
    let (_backend, _root) = selected_session(cx, &["readme.md"]).await;
    cx.update(|cx| {
        WorkspaceCoordinator::init(cx);
    });
    let (pane, cx) = cx.add_window_view(|_, cx| FilesPane::new(cx));
    pane.update_in(cx, |pane, window, cx| {
        BasePanel::set_active(pane, true, window, cx)
    });
    pane.read_with(cx, |pane, _| {
        assert_eq!(pane.message(), Some(PanelMessage::Waiting));
        assert!(!pane.is_loading());
    });
}

#[gpui::test]
async fn a_session_with_a_folder_but_no_recorded_root_lists_it(cx: &mut TestAppContext) {
    let (_backend, _root) = selected_session(cx, &["readme.md"]).await;
    let session = selected_id(cx);
    cx.update(|cx| {
        WorkspaceCoordinator::init(cx);
    });
    let (pane, cx) = cx.add_window_view(|_, cx| FilesPane::new(cx));
    pane.update_in(cx, |pane, window, cx| {
        BasePanel::set_active(pane, true, window, cx)
    });
    // As the coordinator commits a transcript-only target.
    pane.update_in(cx, |pane, window, cx| {
        WorkspacePanes::deactivate(window, cx);
        pane.deactivate_workspace_tree(cx);
    });
    settled(&pane, cx, |pane| {
        !pane.is_loading() && pane.message().is_none()
    })
    .await;
    pane.read_with(cx, |pane, _| {
        assert!(
            shape(pane).contains(&"readme.md".into()),
            "{:?}",
            shape(pane)
        );
    });
    // And a file from that tree reads: the view asks the backend too.
    let (view, cx) = cx
        .cx
        .add_window_view(|_, cx| FileView::new(session.clone(), "readme.md".into(), cx));
    view.update_in(cx, |view, window, cx| {
        BasePanel::set_active(view, true, window, cx)
    });
    settled(&view, cx, |view| view.loading.is_none()).await;
    view.read_with(cx, |view, cx| {
        let text = view
            .document_snapshot(cx)
            .map(|snapshot| snapshot.text)
            .or_else(|| view.text.content().map(String::from));
        assert_eq!(text.as_deref(), Some("x"));
    });
}

#[gpui::test]
async fn a_session_gaining_a_checkout_moves_to_its_workspace_tree(cx: &mut TestAppContext) {
    let (_backend, root) = selected_session(cx, &["src/main.rs", "readme.md"]).await;
    let session = selected_id(cx);
    cx.update(|cx| {
        WorkspaceCoordinator::init(cx);
    });
    let (pane, cx) = cx.add_window_view(|_, cx| FilesPane::new(cx));
    pane.update_in(cx, |pane, window, cx| {
        BasePanel::set_active(pane, true, window, cx);
        WorkspacePanes::deactivate(window, cx);
        pane.deactivate_workspace_tree(cx);
    });
    settled(&pane, cx, |pane| {
        !pane.is_loading() && pane.message().is_none()
    })
    .await;
    let owner = pane.update(cx, |pane, _| pane.owner_for_test());
    let key = WorkspaceKey {
        machine: owner.machine,
        session_id: session,
        checkout_root: root,
    };
    pane.update(cx, |pane, cx| {
        WorkspacePanes::set_key(key.clone(), cx);
        pane.restore_workspace_tree(
            key.clone(),
            TreeSnapshot {
                expanded_paths: vec!["src".into()],
                selected_path: None,
                scroll: 0.,
            },
            cx,
        );
    });
    settled(&pane, cx, |pane| {
        !pane.is_loading() && shape(pane).contains(&"  main.rs".into())
    })
    .await;
    pane.read_with(cx, |pane, _| {
        assert!(pane.workspace_tree(&key).is_some());
    });
}

#[gpui::test]
async fn a_folder_made_by_the_first_message_appears_without_a_click(cx: &mut TestAppContext) {
    let (_backend, root) = selected_session(cx, &["readme.md"]).await;
    let session = selected_id(cx);
    let away = format!("{root}.away");
    fs::rename(&root, &away).unwrap();
    cx.update(Sessions::start);
    let (pane, cx) = cx.add_window_view(|_, cx| FilesPane::new(cx));
    pane.update_in(cx, |pane, window, cx| {
        BasePanel::set_active(pane, true, window, cx)
    });
    settled(&pane, cx, |pane| {
        pane.message() == Some(PanelMessage::FolderNotMade)
    })
    .await;
    fs::rename(&away, &root).unwrap();
    let call = cx.read(|cx| {
        SocketClient::try_global(cx)
            .unwrap()
            .read(cx)
            .call::<Value>(
                "send",
                "send",
                json!({ "session": session, "text": "hi" }),
                None,
            )
    });
    let _ = call.await;
    settled(&pane, cx, |pane| {
        !pane.is_loading() && pane.message().is_none() && shape(pane).contains(&"readme.md".into())
    })
    .await;
    cx.background_executor.timer(Duration::ZERO).await;
}

#[gpui::test]
async fn try_again_after_a_listing_failure_lists_the_folder(cx: &mut TestAppContext) {
    let (_backend, root) = selected_session(cx, &["readme.md"]).await;
    let away = format!("{root}.away");
    fs::rename(&root, &away).unwrap();
    let (pane, cx) = cx.add_window_view(|_, cx| FilesPane::new(cx));
    pane.update_in(cx, |pane, window, cx| {
        BasePanel::set_active(pane, true, window, cx)
    });
    settled(&pane, cx, |pane| {
        pane.message().is_some() && !pane.is_loading()
    })
    .await;
    fs::rename(&away, &root).unwrap();
    pane.update(cx, |pane, cx| pane.retry(cx));
    settled(&pane, cx, |pane| {
        !pane.is_loading() && pane.message().is_none()
    })
    .await;
    pane.read_with(cx, |pane, _| {
        assert!(shape(pane).contains(&"readme.md".into()));
    });
}

#[gpui::test]
async fn an_emptied_folder_says_so_with_its_buttons(cx: &mut TestAppContext) {
    let (_backend, root, session, pane, cx) = shown_pane(cx, &[]).await;
    fs::remove_file(Path::new(&root).join(".gitignore")).unwrap();
    fs::remove_dir_all(Path::new(&root).join(".git")).unwrap();
    cx.update(|_, cx| FolderTouched::touch(&session, cx));
    settled(&pane, cx, |pane| {
        !pane.is_loading() && pane.message() == Some(PanelMessage::EmptyFolder)
    })
    .await;
}
