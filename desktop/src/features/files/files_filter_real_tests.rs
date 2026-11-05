//! The Files tab's filter against the real backend: names found, older runs released, and a click that shows the
//! result in the tree.
use gpui::TestAppContext;
use gpui_kit::component::dock::BasePanel;
use gpui_kit::{Entity, VisualTestContext};
use serde_json::{Value, json};

use super::file_filter::FileFilter;
use super::files_pane::FilesPane;
use super::files_real_tests::{settled, shape};
use super::files_reload_real_tests::shown_pane;
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::viewed_project::{Viewed, ViewedProject};

fn filter_of(pane: &Entity<FilesPane>, cx: &mut VisualTestContext) -> Entity<FileFilter> {
    pane.read_with(cx, |pane, _| pane.filter.clone())
}

/// What the filter found, by path, sorted.
fn found(filter: &FileFilter) -> Vec<String> {
    let mut found: Vec<_> = filter
        .matches()
        .iter()
        .map(|found| found.relative_path.clone())
        .collect();
    found.sort();
    found
}

async fn typed(
    pane: &Entity<FilesPane>,
    query: &str,
    cx: &mut VisualTestContext,
) -> Entity<FileFilter> {
    let filter = filter_of(pane, cx);
    filter.update(cx, |filter, cx| filter.set_query(query, cx));
    settled(&filter, cx, |filter| !filter.searching()).await;
    filter
}

/// Asks the backend for the next result of a run, as a later page would.
async fn continue_run(
    session: &str,
    query: &str,
    run: &str,
    cx: &mut VisualTestContext,
) -> Outcome<Value> {
    let call = cx.update(|_, cx| {
        SocketClient::global(cx).read(cx).call::<Value>(
            "files",
            "search",
            json!({ "session": session, "query": query, "mode": "name", "searchId": run, "pageSize": 1 }),
            None,
        )
    });
    call.await
}

/// Fails unless the backend lets go of `run`. Each call is its own task on the backend, so a page asked just after the
/// release can be served first, or be cancelled by it mid-read; asking a result at a time, the run's 20 left would
/// still answer all ten asks if it were never released.
async fn assert_released(session: &str, query: &str, run: &str, cx: &mut VisualTestContext) {
    let mut last = String::new();
    for _ in 0..10 {
        match continue_run(session, query, run, cx).await {
            Outcome::Failed(failure) if failure.message.contains("cursor_changed") => return,
            Outcome::Failed(failure) => last = failure.message,
            Outcome::Ok(_) => last = "a page".into(),
            Outcome::Aborted => panic!("the connection dropped"),
        }
        cx.background_executor
            .timer(std::time::Duration::from_millis(50))
            .await;
    }
    panic!("the run is still open on the backend; it last answered {last}");
}

fn items(count: usize, extra: &[&str]) -> Vec<String> {
    (0..count)
        .map(|i| format!("items/item{i:03}.md"))
        .chain(extra.iter().map(|path| path.to_string()))
        .collect()
}

// Pins: files#7, files#8, files#9
#[gpui::test]
async fn a_filter_finds_names_and_a_newer_query_releases_the_older_run(cx: &mut TestAppContext) {
    let files = items(120, &["src/main.rs", "target/main.o", "docs/Main.md"]);
    let files: Vec<&str> = files.iter().map(String::as_str).collect();
    let (_backend, _root, session, pane, cx) = shown_pane(cx, &files).await;
    let filter = typed(&pane, "item", cx).await;
    let older = filter.read_with(cx, |filter, _| {
        assert_eq!(filter.matches().len(), 100);
        filter.run_id().expect("a second page").to_string()
    });
    typed(&pane, "MAIN", cx).await;
    filter.read_with(cx, |filter, _| {
        assert_eq!(found(filter), ["docs/Main.md", "src/main.rs"]);
        assert_eq!((filter.error(), filter.run_id()), (None, None));
    });
    assert_released(&session, "item", &older, cx).await;
    // Load more brings the rest, none twice, and leaves no run open.
    typed(&pane, "item", cx).await;
    filter.update(cx, |filter, cx| filter.more(cx));
    settled(&filter, cx, |filter| !filter.searching()).await;
    filter.read_with(cx, |filter, _| {
        // The folder holding them matches too.
        let mut expected = items(120, &["items"]);
        expected.sort();
        assert_eq!(found(filter), expected);
        assert!(!filter.has_more());
    });
}

#[gpui::test]
async fn hiding_the_tab_drops_a_late_answer_and_releases_the_run(cx: &mut TestAppContext) {
    let files = items(120, &[]);
    let files: Vec<&str> = files.iter().map(String::as_str).collect();
    let (_backend, _root, session, pane, cx) = shown_pane(cx, &files).await;
    let filter = typed(&pane, "item", cx).await;
    let run = filter.read_with(cx, |filter, _| filter.run_id().unwrap().to_string());
    pane.update_in(cx, |pane, window, cx| {
        BasePanel::set_active(pane, false, window, cx)
    });
    assert_released(&session, "item", &run, cx).await;
    // Typed while hidden, nothing is asked; shown again, it searches and its answer is kept.
    filter.update(cx, |filter, cx| filter.set_query("item00", cx));
    filter.read_with(cx, |filter, _| assert!(!filter.searching()));
    pane.update_in(cx, |pane, window, cx| {
        BasePanel::set_active(pane, true, window, cx)
    });
    settled(&filter, cx, |filter| !filter.searching()).await;
    filter.read_with(cx, |filter, _| assert_eq!(filter.matches().len(), 10));
    // Hidden before the answer comes, the answer is dropped.
    filter.update(cx, |filter, cx| filter.set_query("item01", cx));
    pane.update_in(cx, |pane, window, cx| {
        BasePanel::set_active(pane, false, window, cx)
    });
    cx.background_executor
        .timer(super::file_filter::DEBOUNCE * 4)
        .await;
    cx.run_until_parked();
    filter.read_with(cx, |filter, _| {
        assert!(filter.matches().is_empty() && !filter.searching());
    });
}

#[gpui::test]
async fn another_session_empties_the_filter_and_releases_its_run(cx: &mut TestAppContext) {
    let files = items(120, &[]);
    let files: Vec<&str> = files.iter().map(String::as_str).collect();
    let (_backend, _root, session, pane, cx) = shown_pane(cx, &files).await;
    let filter = typed(&pane, "item", cx).await;
    let run = filter.read_with(cx, |filter, _| filter.run_id().unwrap().to_string());
    cx.update(|_, cx| {
        SelectedSession::global(cx).update(cx, |selected, cx| selected.select(None, cx))
    });
    filter.read_with(cx, |filter, _| {
        assert_eq!((filter.query(), filter.available()), ("", false));
        assert!(filter.matches().is_empty() && !filter.searching());
    });
    assert_released(&session, "item", &run, cx).await;
}

#[gpui::test]
async fn clearing_returns_the_tree_with_open_folders_and_selection(cx: &mut TestAppContext) {
    let (_backend, _root, _session, pane, cx) =
        shown_pane(cx, &["src/main.rs", "src/lib.rs"]).await;
    pane.update(cx, |pane, cx| pane.clicked("src".into(), true, cx));
    settled(&pane, cx, |pane| !pane.is_loading()).await;
    pane.update(cx, |pane, cx| pane.clicked("src/main.rs".into(), false, cx));
    let tree = pane.read_with(cx, |pane, _| shape(pane));
    let filter = typed(&pane, "lib", cx).await;
    filter.read_with(cx, |filter, _| assert_eq!(found(filter), ["src/lib.rs"]));
    pane.update(cx, |pane, cx| pane.clear_filter(cx));
    pane.read_with(cx, |pane, cx| {
        assert!(!pane.filter.read(cx).active());
        assert_eq!(shape(pane), tree);
        assert_eq!(pane.selected.as_deref(), Some("src/main.rs"));
    });
}

#[gpui::test]
async fn clicking_a_result_reveals_and_selects_it(cx: &mut TestAppContext) {
    let (_backend, _root, _session, pane, cx) = shown_pane(
        cx,
        &["src/deep/inner/found.rs", "src/deep/other.rs", "readme.md"],
    )
    .await;
    let filter = typed(&pane, "found", cx).await;
    let path = filter.read_with(cx, |filter, _| filter.matches()[0].path.clone());
    pane.update_in(cx, |pane, window, cx| {
        pane.open_result(path, false, window, cx)
    });
    settled(&pane, cx, |pane| {
        !pane.is_loading() && pane.selected.as_deref() == Some("src/deep/inner/found.rs")
    })
    .await;
    pane.read_with(cx, |pane, cx| {
        assert!(!pane.filter.read(cx).active());
        assert_eq!(
            shape(pane),
            [
                "src/ v",
                "  deep/ v",
                "    inner/ v",
                "      found.rs",
                "    other.rs",
                ".gitignore",
                "readme.md"
            ]
        );
    });
}

#[gpui::test]
async fn a_result_on_a_later_page_of_its_folder_still_reveals(cx: &mut TestAppContext) {
    let files: Vec<String> = (0..600)
        .map(|i| format!("big/file{i:03}.md"))
        .chain(["big/zz-last.md".to_string()])
        .collect();
    let files: Vec<&str> = files.iter().map(String::as_str).collect();
    let (_backend, _root, _session, pane, cx) = shown_pane(cx, &files).await;
    let filter = typed(&pane, "zz-last", cx).await;
    let path = filter.read_with(cx, |filter, _| filter.matches()[0].path.clone());
    pane.update_in(cx, |pane, window, cx| {
        pane.open_result(path, false, window, cx)
    });
    settled(&pane, cx, |pane| {
        !pane.is_loading() && pane.selected.as_deref() == Some("big/zz-last.md")
    })
    .await;
    pane.read_with(cx, |pane, _| {
        assert!(shape(pane).contains(&"  zz-last.md".to_string()));
    });
}

#[gpui::test]
async fn the_filter_follows_a_viewed_project(cx: &mut TestAppContext) {
    let (_backend, root, session, pane, cx) = shown_pane(cx, &["readme.md"]).await;
    let filter = typed(&pane, "readme", cx).await;
    let viewed = Viewed {
        project_id: "p1".into(),
        machine: "here".into(),
        folder: root.clone(),
        title: "other".into(),
    };
    cx.update(|_, cx| ViewedProject::set(Some(session.clone()), Some(viewed.clone()), cx));
    filter.read_with(cx, |filter, _| {
        assert_eq!(filter.query(), "");
        assert_eq!(
            filter.target().map(|t| t.session.as_str()),
            Some(viewed.id().as_str())
        );
    });
    cx.update(|_, cx| ViewedProject::set(Some(session.clone()), None, cx));
    filter.read_with(cx, |filter, _| {
        assert_eq!(
            filter.target().map(|t| t.session.as_str()),
            Some(session.as_str())
        );
    });
}
