//! Delete asks first, against the real backend: Cancel keeps the file and sends nothing; Move to Trash moves it.
use std::fs;
use std::path::Path;

use gpui::TestAppContext;
use gpui_kit::component::WindowExt as _;
use gpui_kit::component::dock::BasePanel;
use gpui_kit::{AppContext as _, Entity, VisualTestContext};

use super::super::files_pane::FilesPane;
use super::files_real_tests::{selected_session, settled, shape};
use crate::core::socket::real_backend::RealBackend;

/// A pane shown inside a `Root`, which is what an alert opens in, with its root folder.
async fn pane_in_root<'a>(
    cx: &'a mut TestAppContext,
    files: &[&str],
) -> (
    RealBackend,
    String,
    Entity<FilesPane>,
    &'a mut VisualTestContext,
) {
    let (backend, root) = selected_session(cx, files).await;
    let mut made = None;
    let (_, cx) = cx.add_window_view(|window, cx| {
        let pane = cx.new(FilesPane::new);
        made = Some(pane.clone());
        gpui_kit::base::Root::new(pane, window, cx)
    });
    let pane = made.expect("the window made the pane");
    pane.update_in(cx, |pane, window, cx| {
        BasePanel::set_active(pane, true, window, cx)
    });
    settled(&pane, cx, |pane| !pane.is_loading()).await;
    (backend, root, pane, cx)
}

/// Picks Delete for `name` and waits for the question to show.
fn ask_delete(pane: &Entity<FilesPane>, name: &str, cx: &mut VisualTestContext) {
    pane.update_in(cx, |pane, window, cx| {
        pane.delete_entry(name.to_string(), window, cx)
    });
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(
        cx.update(|window, cx| window.has_active_dialog(cx)),
        "Delete asks first"
    );
}

fn dialog_open(cx: &mut VisualTestContext) -> bool {
    cx.update(|window, cx| window.has_active_dialog(cx))
}

#[gpui::test]
async fn cancelling_delete_keeps_the_file(cx: &mut TestAppContext) {
    let (_backend, root, pane, cx) = pane_in_root(cx, &["keep.md"]).await;
    ask_delete(&pane, "keep.md", cx);
    assert!(
        !pane.read_with(cx, |pane, _| pane.action_running()),
        "nothing runs while it asks"
    );
    cx.simulate_keystrokes("escape");
    cx.run_until_parked();
    assert!(!dialog_open(cx));
    settled(&pane, cx, |pane| {
        !pane.action_running() && !pane.is_loading()
    })
    .await;
    assert!(Path::new(&root).join("keep.md").is_file());
    pane.read_with(cx, |pane, _| {
        assert!(shape(pane).contains(&"keep.md".to_string()));
        assert_eq!(pane.action_error, None);
    });
}

#[gpui::test]
async fn confirming_delete_moves_it_to_the_trash(cx: &mut TestAppContext) {
    let name = format!("trash-me-{}.md", std::process::id());
    let (_backend, root, pane, cx) = pane_in_root(cx, &[name.as_str(), "keep.md"]).await;
    ask_delete(&pane, &name, cx);
    cx.simulate_keystrokes("enter");
    cx.run_until_parked();
    assert!(!dialog_open(cx));
    settled(&pane, cx, |pane| {
        !pane.action_running() && pane.action_idle() && !pane.is_loading()
    })
    .await;
    let gone = !Path::new(&root).join(&name).exists();
    pane.read_with(cx, |pane, _| {
        if gone {
            assert!(!shape(pane).contains(&name));
            assert_eq!(pane.action_error, None);
        } else {
            // A host with no Trash refuses, and says so on the red line; nothing is deleted outright.
            let error = pane.action_error.clone().unwrap_or_default();
            assert!(error.contains("Trash"), "{error}");
        }
    });
    assert!(
        Path::new(&root).join("keep.md").is_file(),
        "only the named file goes"
    );
    // Only this test's own file leaves the Trash it was moved to.
    if let Some(home) = std::env::var_os("HOME") {
        let trash = Path::new(&home).join(".local/share/Trash");
        let _ = fs::remove_file(trash.join("files").join(&name));
        let _ = fs::remove_file(trash.join("info").join(format!("{name}.trashinfo")));
    }
}
