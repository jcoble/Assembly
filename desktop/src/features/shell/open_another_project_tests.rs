use std::collections::HashMap;

use futures_channel::mpsc::unbounded;
use serde_json::json;

use super::{OpenAnotherProject, WorktreeRow, project_groups};
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use crate::core::state::machines::Machines;
use crate::core::state::sessions::Sessions;
use crate::features::projects::projects::ProjectRecord;

fn project(id: &str, machine: &str, root: &str, title: &str) -> ProjectRecord {
    serde_json::from_value(json!({
        "id": id, "machine": machine, "rootPath": root, "title": title, "createdAt": 0,
    }))
    .unwrap()
}

#[test]
fn each_machine_lists_its_projects_with_their_worktrees_under_them() {
    let projects = [
        project("r", "workbox-test", "/srv/remote", "remote"),
        project("a", "local", "/w/alpha", "alpha"),
    ];
    let worktrees = HashMap::from([(
        "a".to_string(),
        vec![
            WorktreeRow {
                path: "/w/alpha".into(),
                branch: Some("trunk".into()),
            },
            WorktreeRow {
                path: "/w/alpha-one".into(),
                branch: Some("feature/one".into()),
            },
            WorktreeRow {
                path: "/w/same".into(),
                branch: Some("same".into()),
            },
        ],
    )]);
    let groups = project_groups(&projects, &["workbox-test".to_string()], &worktrees);
    assert_eq!(groups.len(), 2);
    assert!(groups[0].local);
    let labels: Vec<_> = groups[0]
        .options
        .iter()
        .map(|option| option.label.as_str())
        .collect();
    assert_eq!(labels, ["alpha", "alpha-one — feature/one", "same"]);
    assert_eq!(
        groups[0]
            .options
            .iter()
            .map(|option| option.worktree)
            .collect::<Vec<_>>(),
        [false, true, true]
    );
    assert_eq!(groups[1].label, "workbox-test");
    assert!(!groups[1].local);
}

#[test]
fn a_machine_that_is_not_connected_or_a_removed_project_is_not_offered() {
    let mut removed = project("b", "local", "/w/beta", "beta");
    removed.removed_at = Some(1);
    let projects = [
        project("r", "workbox-test", "/srv/remote", "remote"),
        removed,
    ];
    assert!(project_groups(&projects, &[], &HashMap::new()).is_empty());
}

// Saved with the session's workspace, and it comes back only for that session.
#[gpui::test]
fn what_is_viewed_is_saved_and_comes_back_with_its_session(cx: &mut gpui::TestAppContext) {
    use crate::core::state::selected_session::SelectedSession;
    use crate::core::state::viewed_project::{Viewed, ViewedProject};
    use crate::core::state::workspace_panes::{WorkspaceKey, WorkspacePanes};
    use gpui_kit::EmptyView;

    use super::ViewedProjectCodec;

    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        WorkspacePanes::register(Box::new(ViewedProjectCodec), cx).unwrap();
    });
    let (_, cx) = cx.add_window_view(|_, _| EmptyView);
    cx.update(|window, cx| {
        let viewed = Viewed {
            project_id: "p1".into(),
            machine: "workbox-test".into(),
            folder: "/srv/other".into(),
            title: "other".into(),
        };
        SelectedSession::global(cx)
            .update(cx, |selected, cx| selected.select(Some("s1".into()), cx));
        WorkspacePanes::set_key(
            WorkspaceKey {
                machine: "owner".into(),
                session_id: "s1".into(),
                checkout_root: "/own".into(),
            },
            cx,
        );
        ViewedProject::set(Some("s1".into()), Some(viewed.clone()), cx);
        let saved = WorkspacePanes::capture(window, cx).unwrap();
        assert_eq!(saved["viewedProject"]["machine"], "workbox-test");
        ViewedProject::set(Some("s1".into()), None, cx);
        WorkspacePanes::restore(&saved, window, cx).unwrap();
        assert_eq!(ViewedProject::viewed(cx), Some(viewed));
        WorkspacePanes::restore(&json!({"version":1}), window, cx).unwrap();
        assert_eq!(
            ViewedProject::viewed(cx),
            None,
            "a workspace with none shows the session"
        );
    });
}

/// The window, and so the bar, is built before the backend's socket has started. The popover's first opening may even
/// come before the projects have been read.
#[gpui::test]
async fn a_bar_built_before_the_socket_lists_worktrees_when_opened(cx: &mut gpui::TestAppContext) {
    use crate::core::state::selected_session::SelectedSession;

    let (changed, mut changes) = unbounded();
    let sent = changed.clone();
    let backend = FakeBackend::start(move |frame| {
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
        let _ = sent.unbounded_send(());
        let result = match frame["method"].as_str() {
            Some("list") => json!({"projects":[{
                "id":"a","machine":"local","rootPath":"/w/alpha","title":"alpha","createdAt":1
            }],"next":null}),
            Some("worktrees") => json!({"worktrees":[
                {"path":"/w/alpha","branch":"trunk"},{"path":"/w/alpha-one","branch":"one"}
            ]}),
            _ => json!({}),
        };
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
    });
    let (bar, cx) = cx.add_window_view(OpenAnotherProject::new);
    bar.update_in(cx, |bar, window, cx| bar.set_open(true, window, cx));
    cx.update(|_, cx| {
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
        Sessions::start(cx);
        Machines::start(cx);
    });
    until("the worktrees", &mut changes, &changed, || {
        cx.run_until_parked();
        bar.read_with(cx, |bar, _| bar.worktrees.contains_key("a"))
    })
    .await;
}
