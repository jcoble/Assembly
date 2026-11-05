//! Reloads keep the tree: leaving the tab, a tool changing the folder, or a click during a reload never collapses it.
use super::files_pane::FilesPane;
use super::files_real_tests::{selected_session, settled, shape};
use crate::core::socket::real_backend::RealBackend;
use crate::core::state::folder_touched::FolderTouched;
use crate::core::state::selected_session::SelectedSession;
use gpui::TestAppContext;
use gpui_kit::component::dock::BasePanel;
use gpui_kit::{Entity, VisualTestContext};
use std::fs;
use std::path::Path;

/// A pane on a session holding `files`, shown and settled, with its root and session id.
pub(super) async fn shown_pane<'a>(
    cx: &'a mut TestAppContext,
    files: &[&str],
) -> (
    RealBackend,
    String,
    String,
    Entity<FilesPane>,
    &'a mut VisualTestContext,
) {
    let (backend, root) = selected_session(cx, files).await;
    let session = cx.read(|cx| {
        SelectedSession::global(cx)
            .read(cx)
            .session_id()
            .unwrap()
            .to_string()
    });
    let (pane, cx) = cx.add_window_view(|_, cx| FilesPane::new(cx));
    pane.update_in(cx, |pane, window, cx| {
        BasePanel::set_active(pane, true, window, cx)
    });
    settled(&pane, cx, |pane| !pane.is_loading()).await;
    (backend, root, session, pane, cx)
}

#[gpui::test]
async fn leaving_the_tab_and_returning_keeps_open_folders_and_selection(cx: &mut TestAppContext) {
    let (_backend, root, _session, pane, cx) = shown_pane(cx, &["src/main.rs", "readme.md"]).await;
    pane.update(cx, |pane, cx| pane.clicked("src".into(), true, cx));
    settled(&pane, cx, |pane| !pane.is_loading()).await;
    pane.update(cx, |pane, cx| pane.clicked("src/main.rs".into(), false, cx));
    fs::write(Path::new(&root).join("src/added.rs"), "").unwrap();
    pane.update_in(cx, |pane, window, cx| {
        BasePanel::set_active(pane, false, window, cx)
    });
    pane.read_with(cx, |pane, _| {
        assert!(pane.loads.is_empty());
        assert!(shape(pane).contains(&"  main.rs".into()));
    });
    pane.update_in(cx, |pane, window, cx| {
        BasePanel::set_active(pane, true, window, cx)
    });
    pane.read_with(cx, |pane, _| {
        assert!(
            shape(pane).contains(&"  main.rs".into()),
            "{:?}",
            shape(pane)
        );
    });
    settled(&pane, cx, |pane| {
        !pane.is_loading() && shape(pane).contains(&"  added.rs".into())
    })
    .await;
    pane.read_with(cx, |pane, _| {
        assert_eq!(pane.selected.as_deref(), Some("src/main.rs"));
    });
}

#[gpui::test]
async fn a_touched_folder_keeps_its_rows_until_the_new_tree_is_ready(cx: &mut TestAppContext) {
    let (_backend, root, session, pane, cx) = shown_pane(cx, &["src/main.rs", "readme.md"]).await;
    pane.update(cx, |pane, cx| pane.clicked("src".into(), true, cx));
    settled(&pane, cx, |pane| !pane.is_loading()).await;
    let before = pane.read_with(cx, |pane, _| shape(pane));
    fs::write(Path::new(&root).join("src/added.rs"), "").unwrap();
    cx.update(|_, cx| FolderTouched::touch(&session, cx));
    pane.read_with(cx, |pane, _| {
        assert!(pane.is_loading());
        assert_eq!(
            shape(pane),
            before,
            "a reload changes nothing until it is ready"
        );
    });
    settled(&pane, cx, |pane| !pane.is_loading()).await;
    pane.read_with(cx, |pane, _| {
        assert!(
            shape(pane).contains(&"  added.rs".into()),
            "{:?}",
            shape(pane)
        );
        assert!(shape(pane).iter().all(|line| !line.contains("…")));
    });
}

#[gpui::test]
async fn touches_during_a_reload_cause_one_more_reload(cx: &mut TestAppContext) {
    let (_backend, root, session, pane, cx) = shown_pane(cx, &["readme.md"]).await;
    cx.update(|_, cx| FolderTouched::touch(&session, cx));
    fs::write(Path::new(&root).join("later.md"), "").unwrap();
    cx.update(|_, cx| FolderTouched::touch(&session, cx));
    cx.update(|_, cx| FolderTouched::touch(&session, cx));
    settled(&pane, cx, |pane| {
        !pane.is_loading() && shape(pane).contains(&"later.md".into())
    })
    .await;
}

#[gpui::test]
async fn opening_a_folder_during_a_reload_keeps_it_open(cx: &mut TestAppContext) {
    let (_backend, _root, session, pane, cx) = shown_pane(cx, &["src/main.rs", "readme.md"]).await;
    cx.update(|_, cx| FolderTouched::touch(&session, cx));
    pane.update(cx, |pane, cx| pane.clicked("src".into(), true, cx));
    settled(&pane, cx, |pane| {
        !pane.is_loading() && shape(pane).contains(&"  main.rs".into())
    })
    .await;
    pane.read_with(cx, |pane, _| {
        assert!(shape(pane).contains(&"src/ v".into()));
    });
}

#[gpui::test]
async fn a_selection_removed_during_a_reload_is_cleared(cx: &mut TestAppContext) {
    let (_backend, root, session, pane, cx) = shown_pane(cx, &["readme.md", "gone.md"]).await;
    pane.update(cx, |pane, cx| pane.clicked("gone.md".into(), false, cx));
    fs::remove_file(Path::new(&root).join("gone.md")).unwrap();
    cx.update(|_, cx| FolderTouched::touch(&session, cx));
    settled(&pane, cx, |pane| !pane.is_loading()).await;
    pane.read_with(cx, |pane, _| assert_eq!(pane.selected, None));
}

#[gpui::test]
async fn a_folder_deleted_during_a_reload_shows_its_message(cx: &mut TestAppContext) {
    let (_backend, root, session, pane, cx) = shown_pane(cx, &["readme.md"]).await;
    let away = format!("{root}-away");
    fs::rename(&root, &away).unwrap();
    cx.update(|_, cx| FolderTouched::touch(&session, cx));
    settled(&pane, cx, |pane| !pane.is_loading()).await;
    pane.read_with(cx, |pane, _| {
        assert!(
            shape(pane).iter().any(|line| line.starts_with("!")),
            "{:?}",
            shape(pane)
        );
    });
    fs::rename(&away, &root).unwrap();
}

#[gpui::test]
async fn a_selection_past_the_first_page_survives_a_reload(cx: &mut TestAppContext) {
    let names: Vec<String> = (0..510).map(|i| format!("file-{i:04}")).collect();
    let files: Vec<&str> = names.iter().map(String::as_str).collect();
    let (_backend, _root, session, pane, cx) = shown_pane(cx, &files).await;
    pane.update(cx, |pane, _| pane.selected = Some("file-0505".into()));
    cx.update(|_, cx| FolderTouched::touch(&session, cx));
    settled(&pane, cx, |pane| !pane.is_loading()).await;
    pane.read_with(cx, |pane, _| {
        assert_eq!(pane.selected.as_deref(), Some("file-0505"));
        assert!(shape(pane).contains(&"file-0505".into()));
    });
}

#[gpui::test]
async fn a_listing_that_moved_under_a_page_reloads_and_keeps_the_tree(cx: &mut TestAppContext) {
    let names: Vec<String> = (0..510).map(|i| format!("file-{i:04}")).collect();
    let mut files: Vec<&str> = names.iter().map(String::as_str).collect();
    files.push("aaa/inner.rs");
    let (_backend, root, _session, pane, cx) = shown_pane(cx, &files).await;
    pane.update(cx, |pane, cx| pane.clicked("aaa".into(), true, cx));
    settled(&pane, cx, |pane| !pane.is_loading()).await;
    fs::write(Path::new(&root).join("aaa/added.rs"), "").unwrap();
    pane.update(cx, |pane, cx| {
        let mut cursor = pane.tree.more("").expect("a second page");
        cursor.generation = "an older listing".into();
        pane.load(String::new(), Some(cursor), cx);
    });
    settled(&pane, cx, |pane| {
        !pane.is_loading() && shape(pane).contains(&"  added.rs".into())
    })
    .await;
    pane.read_with(cx, |pane, _| {
        assert!(
            shape(pane).contains(&"aaa/ v".into()),
            "{:?}",
            &shape(pane)[..3]
        );
        assert!(shape(pane).contains(&"  inner.rs".into()));
    });
}
