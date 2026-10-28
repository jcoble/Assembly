//! The Files and Search panes against the real desktop backend: a session made there, its folder filled as a git
//! repo, then the tree and the search read from it.

use std::fs;
use std::path::Path;
use std::process::Command;
use std::time::Duration;

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::component::dock::BasePanel;
use gpui_kit::{Entity, VisualTestContext};
use serde_json::{Value, json};

use super::file_tree::Row;
use super::files_pane::FilesPane;
use super::search_pane::{DEBOUNCE, SearchPane};
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::real_backend::RealBackend;
use crate::core::socket::real_backend_cancel_tests::backend_with_stub;
use crate::core::socket::test_wait::until;
use crate::core::state::selected_session::SelectedSession;

/// A backend with one session selected, whose folder is a git repo holding `files` (a path ending in '/' is a folder)
/// and a `.gitignore` that ignores `target/`.
async fn selected_session(
    cx: &mut TestAppContext,
    files: &[&str],
) -> Option<(RealBackend, String)> {
    let (backend, repo) = backend_with_stub()?;
    cx.executor().allow_parking();
    cx.update(gpui_kit::init);
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let (socket, _heard) = cx.update(|cx| {
        SelectedSession::init(cx);
        let socket = SocketClient::start(backend.endpoint(), cx);
        let heard = cx.observe(&socket, move |_, _| _ = tell.unbounded_send(()));
        (socket, heard)
    });
    until("connected", &mut changes, &changed, || {
        cx.read(|cx| matches!(socket.read(cx).state(), ConnectionState::Connected { .. }))
    })
    .await;
    let call = |method: &str, args: Value, cx: &mut TestAppContext| {
        cx.read(|cx| {
            socket
                .read(cx)
                .call::<Value>("sessions", method, args, None)
        })
    };
    let Outcome::Ok(created) =
        call("create", json!({ "agent": "stub", "repoUrl": repo }), cx).await
    else {
        panic!("the session wasn't made");
    };
    let id = created["sessionId"].as_str().unwrap().to_string();
    let Outcome::Ok(page) = call("page", json!({ "limit": 10 }), cx).await else {
        panic!("no sessions page");
    };
    let root = page["sessions"][0]["root"]
        .as_str()
        .expect("the session's root")
        .to_string();
    fill(Path::new(&root), files);
    cx.update(|cx| {
        SelectedSession::global(cx).update(cx, |selected, cx| selected.select(Some(id), cx))
    });
    Some((backend, root))
}

fn fill(root: &Path, files: &[&str]) {
    fs::create_dir_all(root).unwrap();
    assert!(
        Command::new("git")
            .args(["init", "--quiet"])
            .current_dir(root)
            .status()
            .unwrap()
            .success()
    );
    fs::write(root.join(".gitignore"), "target/\n").unwrap();
    for file in files {
        let path = root.join(file);
        if file.ends_with('/') {
            fs::create_dir_all(path).unwrap();
        } else {
            fs::create_dir_all(path.parent().unwrap()).unwrap();
            fs::write(path, "x").unwrap();
        }
    }
}

/// Waits for the view to change until `done` holds.
async fn settled<T: 'static>(
    view: &Entity<T>,
    cx: &mut VisualTestContext,
    done: impl Fn(&T) -> bool,
) {
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _heard = cx.update(|_, cx| cx.observe(view, move |_, _| _ = tell.unbounded_send(())));
    until("the pane", &mut changes, &changed, || {
        view.read_with(cx, |view, _| done(view))
    })
    .await;
}

/// The tree's lines as text: an open folder ends in "/ v", an excluded entry in " -".
fn shape(pane: &FilesPane) -> Vec<String> {
    pane.tree
        .rows()
        .iter()
        .map(|row| match row {
            Row::Entry { entry, depth, open } => format!(
                "{}{}{}{}",
                "  ".repeat(*depth),
                entry.name,
                if *open {
                    "/ v"
                } else if entry.is_directory {
                    "/"
                } else {
                    ""
                },
                if entry.excluded { " -" } else { "" }
            ),
            Row::Loading { .. } => "…".into(),
            Row::Failed { message, .. } => format!("! {message}"),
            Row::Truncated { .. } => "+".into(),
        })
        .collect()
}

// Pins: files#1, files#2, files#3, files#5
#[gpui::test]
async fn the_tree_loads_when_shown_and_opens_folders_one_at_a_time(cx: &mut TestAppContext) {
    let Some((_backend, _)) = selected_session(
        cx,
        &["src/main.rs", "src/lib/", "target/out.bin", "readme.md"],
    )
    .await
    else {
        return;
    };
    let (pane, cx) = cx.add_window_view(|_, cx| FilesPane::new(cx));
    // Hidden, it asks for nothing.
    pane.read_with(cx, |pane, _| {
        assert!(pane.tree.rows().is_empty() && pane.loads.is_empty())
    });
    pane.update_in(cx, |pane, window, cx| {
        BasePanel::set_active(pane, true, window, cx)
    });
    settled(&pane, cx, |pane| pane.loads.is_empty()).await;
    pane.read_with(cx, |pane, _| {
        assert_eq!(
            shape(pane),
            [".git/ -", "src/", "target/ -", ".gitignore", "readme.md"]
        );
    });
    pane.update(cx, |pane, cx| pane.clicked("src".into(), true, cx));
    settled(&pane, cx, |pane| pane.loads.is_empty()).await;
    pane.read_with(cx, |pane, _| {
        assert_eq!(
            shape(pane),
            [
                ".git/ -",
                "src/ v",
                "  lib/",
                "  main.rs",
                "target/ -",
                ".gitignore",
                "readme.md"
            ]
        );
        assert_eq!(pane.selected.as_deref(), Some("src"));
    });
}

// Pins: files#7, files#8
#[gpui::test]
async fn search_finds_names_and_a_newer_search_replaces_the_older(cx: &mut TestAppContext) {
    let Some((_backend, _)) = selected_session(
        cx,
        &["src/main.rs", "src/lib.rs", "target/main.o", "docs/Main.md"],
    )
    .await
    else {
        return;
    };
    let (pane, cx) = cx.add_window_view(SearchPane::new);
    pane.update(cx, |pane, cx| {
        pane.search("lib".into(), DEBOUNCE, cx);
        pane.search("MAIN".into(), Duration::ZERO, cx);
    });
    settled(&pane, cx, |pane| pane.searching.is_none()).await;
    pane.read_with(cx, |pane, _| {
        let mut found: Vec<_> = pane
            .matches
            .iter()
            .map(|found| found.relative_path.as_str())
            .collect();
        found.sort();
        assert_eq!(found, ["docs/Main.md", "src/main.rs"]);
        assert!(pane.error.is_none());
    });
}

// Pins: files#6
#[gpui::test]
async fn a_folder_made_after_the_tab_was_shown_loads_when_shown_again(cx: &mut TestAppContext) {
    let Some((_backend, root)) = selected_session(cx, &["readme.md"]).await else {
        return;
    };
    // As before the session's first message: no folder yet.
    let away = format!("{root}.away");
    fs::rename(&root, &away).unwrap();
    let (pane, cx) = cx.add_window_view(|_, cx| FilesPane::new(cx));
    pane.update_in(cx, |pane, window, cx| {
        BasePanel::set_active(pane, true, window, cx)
    });
    settled(&pane, cx, |pane| pane.loads.is_empty()).await;
    pane.read_with(cx, |pane, _| {
        assert_eq!(
            shape(pane),
            ["! this session's folder is made on its first message"]
        );
    });
    fs::rename(&away, &root).unwrap();
    pane.update_in(cx, |pane, window, cx| {
        BasePanel::set_active(pane, false, window, cx);
        BasePanel::set_active(pane, true, window, cx);
    });
    settled(&pane, cx, |pane| pane.loads.is_empty()).await;
    pane.read_with(cx, |pane, _| {
        assert_eq!(shape(pane), [".git/ -", ".gitignore", "readme.md"])
    });
}
