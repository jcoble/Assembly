//! Changes made on disk outside the app reach the tree through `filesChanged`, and only for folders it shows.
use std::fs;
use std::path::Path;
use std::time::Duration;

use gpui::TestAppContext;
use gpui_kit::{Entity, VisualTestContext};

use super::super::files_pane::FilesPane;
use super::files_real_tests::{settled, shape};
use super::files_reload_real_tests::shown_pane;

/// Waits well past the backend's 350 ms window, so a change it would send has come.
async fn quiet(pane: &Entity<FilesPane>, cx: &mut VisualTestContext) {
    cx.background_executor
        .timer(Duration::from_millis(1500))
        .await;
    settled(pane, cx, |pane| !pane.is_loading()).await;
}

fn listings(pane: &Entity<FilesPane>, cx: &mut VisualTestContext) -> usize {
    pane.read_with(cx, |pane, _| pane.listings)
}

#[gpui::test]
async fn a_file_made_outside_the_app_appears_in_the_tree(cx: &mut TestAppContext) {
    let (_backend, root, _session, pane, cx) = shown_pane(cx, &["readme.md"]).await;
    fs::write(Path::new(&root).join("added.md"), "").unwrap();
    settled(&pane, cx, |pane| {
        shape(pane).contains(&"added.md".to_string())
    })
    .await;
}

#[gpui::test]
async fn a_change_in_a_closed_folder_sends_no_listing(cx: &mut TestAppContext) {
    let (_backend, root, _session, pane, cx) = shown_pane(cx, &["src/main.rs", "readme.md"]).await;
    // src is loaded, then closed: its entries aren't on screen.
    pane.update(cx, |pane, cx| pane.clicked("src".into(), true, cx));
    settled(&pane, cx, |pane| !pane.is_loading()).await;
    pane.update(cx, |pane, cx| pane.clicked("src".into(), true, cx));
    let before = listings(&pane, cx);
    fs::write(Path::new(&root).join("src/added.rs"), "").unwrap();
    quiet(&pane, cx).await;
    assert_eq!(listings(&pane, cx), before);
    // A change the tree shows still reads it, so the quiet above wasn't a missing subscription.
    fs::write(Path::new(&root).join("top.md"), "").unwrap();
    settled(&pane, cx, |pane| {
        shape(pane).contains(&"top.md".to_string())
    })
    .await;
    assert!(listings(&pane, cx) > before);
}

#[gpui::test]
async fn subscribing_does_not_list_twice(cx: &mut TestAppContext) {
    let (_backend, _root, _session, pane, cx) = shown_pane(cx, &["readme.md"]).await;
    assert_eq!(listings(&pane, cx), 1);
    quiet(&pane, cx).await;
    assert_eq!(listings(&pane, cx), 1);
}
