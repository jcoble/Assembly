//! The history card against the real backend: it reads only while open, follows the selection and the editor's file,
//! and never shows another file's commits.
use std::fs;
use std::path::Path;
use std::process::Command;
use std::sync::{Arc, Mutex};

use gpui::TestAppContext;
use gpui_kit::{Entity, VisualTestContext};

use super::super::file_entry_bar::EntryKind;
use super::super::file_history::{Body, FileHistoryList};
use super::super::files_pane::FilesPane;
use super::files_real_tests::settled;
use super::files_reload_real_tests::shown_pane;
use crate::core::intents::open_commit_diff::OpenCommitDiff;
use crate::core::state::active_file::ActiveFile;
use crate::core::state::folder_touched::FolderTouched;

fn git(root: &str, args: &[&str]) {
    let done = Command::new("git")
        .args(["-c", "user.name=Test", "-c", "user.email=test@example.com"])
        .args(args)
        .current_dir(root)
        .status()
        .unwrap();
    assert!(done.success(), "git {args:?} failed");
}

/// Writes `text` to `file` and commits it as `subject`.
fn commit(root: &str, file: &str, text: &str, subject: &str) {
    // Each file gets its own text: Git follows a file through renames, and two equal files look like one.
    fs::write(Path::new(root).join(file), format!("{file}: {text}")).unwrap();
    git(root, &["add", file]);
    git(root, &["commit", "--quiet", "-m", subject]);
}

fn history_of(pane: &Entity<FilesPane>, cx: &mut VisualTestContext) -> Entity<FileHistoryList> {
    pane.read_with(cx, |pane, _| pane.history.clone())
}

fn subjects(list: &FileHistoryList) -> Vec<String> {
    list.commits()
        .iter()
        .map(|commit| commit.subject.clone())
        .collect()
}

fn shown(list: &FileHistoryList) -> Option<String> {
    list.file().map(|file| file.path.clone())
}

/// Waits until the card has read what it shows.
async fn read(list: &Entity<FileHistoryList>, cx: &mut VisualTestContext) {
    settled(list, cx, |list| {
        !list.is_reading() && list.body() != Body::Reading
    })
    .await;
}

fn select(pane: &Entity<FilesPane>, path: &str, cx: &mut VisualTestContext) {
    pane.update(cx, |pane, cx| pane.clicked(path.into(), false, cx));
}

fn open_card(pane: &Entity<FilesPane>, cx: &mut VisualTestContext) {
    pane.update(cx, |pane, cx| pane.toggle_history(cx));
    pane.read_with(cx, |pane, cx| assert!(pane.history_open(cx)));
}

#[gpui::test]
async fn collapsed_card_sends_no_history_call(cx: &mut TestAppContext) {
    let (_backend, root, _session, pane, cx) = shown_pane(cx, &["a.md"]).await;
    commit(&root, "a.md", "one", "first a");
    select(&pane, "a.md", cx);
    let list = history_of(&pane, cx);
    list.read_with(cx, |list, _| {
        assert_eq!(shown(list).as_deref(), Some("a.md"));
        assert!(!list.is_reading() && list.commits().is_empty());
    });
    open_card(&pane, cx);
    list.read_with(cx, |list, _| assert!(list.is_reading()));
    // Collapsing drops the read before it answers.
    pane.update(cx, |pane, cx| pane.toggle_history(cx));
    list.read_with(cx, |list, _| {
        assert!(!list.is_reading() && list.commits().is_empty())
    });
}

#[gpui::test]
async fn opening_loads_the_selected_files_commits(cx: &mut TestAppContext) {
    let (_backend, root, _session, pane, cx) = shown_pane(cx, &["a.md", "b.md"]).await;
    commit(&root, "a.md", "one", "first a");
    commit(&root, "b.md", "one", "only b");
    commit(&root, "a.md", "two", "second a");
    select(&pane, "a.md", cx);
    open_card(&pane, cx);
    let list = history_of(&pane, cx);
    read(&list, cx).await;
    list.read_with(cx, |list, _| {
        assert_eq!(list.body(), Body::Commits);
        assert_eq!(subjects(list), ["second a", "first a"]);
    });
}

#[gpui::test]
async fn switching_editor_tab_moves_the_card(cx: &mut TestAppContext) {
    let (_backend, root, session, pane, cx) = shown_pane(cx, &["a.md", "b.md"]).await;
    commit(&root, "a.md", "one", "only a");
    commit(&root, "b.md", "one", "only b");
    select(&pane, "a.md", cx);
    open_card(&pane, cx);
    let list = history_of(&pane, cx);
    read(&list, cx).await;
    let source = list.read_with(cx, |list, _| list.file().unwrap().source.clone());
    cx.update(|_, cx| ActiveFile::set(&session, "b.md", source.clone(), cx));
    read(&list, cx).await;
    list.read_with(cx, |list, _| {
        assert_eq!(shown(list).as_deref(), Some("b.md"));
        assert_eq!(subjects(list), ["only b"]);
    });
    // A tab of another session's file isn't this tree's.
    cx.update(|_, cx| ActiveFile::set("another-session", "a.md", source, cx));
    list.read_with(cx, |list, _| {
        assert_eq!(list.body(), Body::Pick);
        assert!(list.commits().is_empty());
    });
}

#[gpui::test]
async fn a_deleted_file_shows_no_other_files_commits(cx: &mut TestAppContext) {
    let (_backend, root, session, pane, cx) = shown_pane(cx, &["a.md", "b.md"]).await;
    commit(&root, "a.md", "one", "only a");
    commit(&root, "b.md", "one", "only b");
    let list = history_of(&pane, cx);
    select(&pane, "b.md", cx);
    let source = list.read_with(cx, |list, _| list.file().unwrap().source.clone());
    cx.update(|_, cx| ActiveFile::set(&session, "b.md", source, cx));
    select(&pane, "a.md", cx);
    open_card(&pane, cx);
    read(&list, cx).await;
    list.read_with(cx, |list, _| assert_eq!(subjects(list), ["only a"]));
    fs::remove_file(Path::new(&root).join("a.md")).unwrap();
    cx.update(|_, cx| FolderTouched::touch(&session, cx));
    settled(&pane, cx, |pane| {
        pane.selected.is_none() && !pane.is_loading()
    })
    .await;
    read(&list, cx).await;
    list.read_with(cx, |list, _| {
        assert_eq!(shown(list).as_deref(), Some("b.md"));
        assert_eq!(subjects(list), ["only b"]);
    });
}

#[gpui::test]
async fn renaming_the_selected_file_moves_the_card(cx: &mut TestAppContext) {
    let (_backend, root, _session, pane, cx) = shown_pane(cx, &["a.md"]).await;
    commit(&root, "a.md", "one", "only a");
    select(&pane, "a.md", cx);
    open_card(&pane, cx);
    let list = history_of(&pane, cx);
    read(&list, cx).await;
    pane.update_in(cx, |pane, window, cx| {
        pane.start_entry(EntryKind::Rename, "a.md", window, cx);
        let input = pane.entry.as_ref().expect("the bar is open").input.clone();
        input.update(cx, |input, cx| input.set_value("c.md", window, cx));
        pane.commit_entry(&input, window, cx);
    });
    settled(&list, cx, |list| {
        list.file().is_some_and(|file| file.path == "c.md")
            && !list.is_reading()
            && list.body() != Body::Reading
    })
    .await;
    // The move isn't committed, so Git has no commits for the new name, and the old name's aren't shown.
    list.read_with(cx, |list, _| assert_eq!(list.body(), Body::Empty));
}

#[gpui::test]
async fn closing_the_editor_tab_falls_back_to_the_selection(cx: &mut TestAppContext) {
    let (_backend, root, session, pane, cx) = shown_pane(cx, &["a.md", "b.md"]).await;
    commit(&root, "a.md", "one", "only a");
    commit(&root, "b.md", "one", "only b");
    select(&pane, "a.md", cx);
    open_card(&pane, cx);
    let list = history_of(&pane, cx);
    read(&list, cx).await;
    let source = list.read_with(cx, |list, _| list.file().unwrap().source.clone());
    cx.update(|_, cx| ActiveFile::set(&session, "b.md", source, cx));
    read(&list, cx).await;
    list.read_with(cx, |list, _| assert_eq!(subjects(list), ["only b"]));
    cx.update(|_, cx| ActiveFile::clear(&session, "b.md", cx));
    read(&list, cx).await;
    list.read_with(cx, |list, _| {
        assert_eq!(shown(list).as_deref(), Some("a.md"));
        assert_eq!(subjects(list), ["only a"]);
    });
}

#[gpui::test]
async fn scrolling_to_the_end_loads_older_without_duplicates(cx: &mut TestAppContext) {
    let (_backend, root, _session, pane, cx) = shown_pane(cx, &["a.md"]).await;
    for i in 0..30 {
        commit(&root, "a.md", &format!("{i}"), &format!("change {i}"));
    }
    select(&pane, "a.md", cx);
    open_card(&pane, cx);
    let list = history_of(&pane, cx);
    read(&list, cx).await;
    list.read_with(cx, |list, _| assert_eq!(list.commits().len(), 24));
    list.update(cx, |list, cx| list.reached(10, cx));
    list.read_with(cx, |list, _| assert!(!list.is_reading()));
    list.update(cx, |list, cx| list.reached(23, cx));
    read(&list, cx).await;
    list.read_with(cx, |list, _| {
        let mut shas: Vec<_> = list.commits().iter().map(|commit| &commit.sha).collect();
        assert_eq!(shas.len(), 30);
        shas.sort();
        shas.dedup();
        assert_eq!(shas.len(), 30);
        assert_eq!(subjects(list).last().map(String::as_str), Some("change 0"));
    });
}

#[gpui::test]
async fn a_folder_outside_git_says_unavailable(cx: &mut TestAppContext) {
    let (_backend, root, _session, pane, cx) = shown_pane(cx, &["a.md"]).await;
    fs::remove_dir_all(Path::new(&root).join(".git")).unwrap();
    select(&pane, "a.md", cx);
    open_card(&pane, cx);
    let list = history_of(&pane, cx);
    read(&list, cx).await;
    list.read_with(cx, |list, _| assert_eq!(list.body(), Body::Unavailable));
}

#[gpui::test]
async fn clicking_a_commit_opens_its_diff(cx: &mut TestAppContext) {
    let (_backend, root, session, pane, cx) = shown_pane(cx, &["a.md"]).await;
    commit(&root, "a.md", "one", "only a");
    let opened: Arc<Mutex<Vec<OpenCommitDiff>>> = Arc::default();
    let heard = opened.clone();
    cx.update(|_, cx| {
        cx.on_action(move |intent: &OpenCommitDiff, _| heard.lock().unwrap().push(intent.clone()));
    });
    select(&pane, "a.md", cx);
    open_card(&pane, cx);
    let list = history_of(&pane, cx);
    read(&list, cx).await;
    let sha = list.read_with(cx, |list, _| list.commits()[0].sha.clone());
    list.update_in(cx, |list, window, cx| list.open(0, window, cx));
    cx.run_until_parked();
    let opened = opened.lock().unwrap();
    assert_eq!(opened.len(), 1);
    assert_eq!(
        (
            opened[0].session_id.as_str(),
            opened[0].sha.as_str(),
            opened[0].path.as_str()
        ),
        (session.as_str(), sha.as_str(), "a.md")
    );
}

#[gpui::test]
async fn open_timeline_selects_opens_and_shows_the_file(cx: &mut TestAppContext) {
    let (_backend, root, session, pane, cx) = shown_pane(cx, &["a.md", "b.md"]).await;
    commit(&root, "a.md", "one", "only a");
    commit(&root, "b.md", "one", "only b");
    let list = history_of(&pane, cx);
    // The editor shows a.md, and was the last to change; Open Timeline on b.md still shows b.md.
    select(&pane, "b.md", cx);
    let source = list.read_with(cx, |list, _| list.file().unwrap().source.clone());
    cx.update(|_, cx| ActiveFile::set(&session, "a.md", source, cx));
    pane.update(cx, |pane, cx| pane.open_timeline("b.md".into(), cx));
    pane.read_with(cx, |pane, cx| {
        assert_eq!(pane.selected.as_deref(), Some("b.md"));
        assert!(pane.history_open(cx));
    });
    read(&list, cx).await;
    list.read_with(cx, |list, _| {
        assert_eq!(shown(list).as_deref(), Some("b.md"));
        assert_eq!(subjects(list), ["only b"]);
    });
}
