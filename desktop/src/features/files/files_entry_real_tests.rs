//! The entry bar and the menu's writes against the real backend: New, Rename, Delete, Cut, Copy and Paste, with no
//! dialog.
use std::fs;
use std::path::Path;

use gpui::TestAppContext;
use gpui_kit::{Entity, VisualTestContext};

use super::file_entry_bar::EntryKind;
use super::files_header_real_tests::make_read_only;
use super::files_pane::FilesPane;
use super::files_real_tests::{settled, shape};
use super::files_reload_real_tests::shown_pane;

/// Types `name` into the open bar and presses Enter.
fn commit(pane: &Entity<FilesPane>, name: &str, cx: &mut VisualTestContext) {
    pane.update_in(cx, |pane, window, cx| {
        let input = pane.entry.as_ref().expect("the bar is open").input.clone();
        input.update(cx, |input, cx| {
            input.set_value(name.to_string(), window, cx)
        });
        pane.commit_entry(&input, window, cx);
    });
}

async fn done(pane: &Entity<FilesPane>, cx: &mut VisualTestContext) {
    settled(pane, cx, |pane| {
        !pane.action_running() && pane.action_idle() && !pane.is_loading()
    })
    .await;
}

#[gpui::test]
async fn new_file_from_a_closed_folder_opens_it_and_shows_the_file(cx: &mut TestAppContext) {
    let (_backend, root, _session, pane, cx) = shown_pane(cx, &["src/main.rs"]).await;
    pane.update_in(cx, |pane, window, cx| {
        pane.start_entry(EntryKind::NewFile, "src", window, cx)
    });
    settled(&pane, cx, |pane| !pane.is_loading()).await;
    pane.read_with(cx, |pane, cx| {
        assert!(shape(pane).contains(&"src/ v".to_string()));
        let entry = pane.entry.as_ref().unwrap();
        assert_eq!(entry.input.read(cx).value().as_ref(), "");
    });
    commit(&pane, "added.rs", cx);
    settled(&pane, cx, |pane| {
        pane.entry.is_none() && shape(pane).contains(&"  added.rs".to_string())
    })
    .await;
    pane.read_with(cx, |pane, _| {
        assert_eq!(pane.selected.as_deref(), Some("src/added.rs"));
    });
    assert!(Path::new(&root).join("src/added.rs").is_file());
}

#[gpui::test]
async fn an_empty_name_closes_the_bar_and_makes_nothing(cx: &mut TestAppContext) {
    let (_backend, _root, _session, pane, cx) = shown_pane(cx, &["readme.md"]).await;
    let before = pane.read_with(cx, |pane, _| shape(pane));
    pane.update_in(cx, |pane, window, cx| {
        pane.start_entry(EntryKind::NewFolder, "", window, cx)
    });
    commit(&pane, "  ", cx);
    pane.read_with(cx, |pane, _| {
        assert!(pane.entry.is_none() && !pane.action_running());
        assert_eq!(shape(pane), before);
    });
}

#[gpui::test]
async fn rename_starts_with_the_name_and_moves_the_file(cx: &mut TestAppContext) {
    let (_backend, root, _session, pane, cx) = shown_pane(cx, &["notes.md"]).await;
    pane.update_in(cx, |pane, window, cx| {
        pane.start_entry(EntryKind::Rename, "notes.md", window, cx)
    });
    pane.read_with(cx, |pane, cx| {
        let entry = pane.entry.as_ref().unwrap();
        assert_eq!(entry.input.read(cx).value().as_ref(), "notes.md");
    });
    commit(&pane, "ideas.md", cx);
    settled(&pane, cx, |pane| {
        pane.entry.is_none() && shape(pane).contains(&"ideas.md".to_string())
    })
    .await;
    pane.read_with(cx, |pane, _| {
        assert!(!shape(pane).contains(&"notes.md".to_string()));
        assert_eq!(pane.selected.as_deref(), Some("ideas.md"));
    });
    assert!(Path::new(&root).join("ideas.md").is_file());
}

#[gpui::test]
async fn rename_collision_keeps_the_bar_and_says_why(cx: &mut TestAppContext) {
    let (_backend, root, _session, pane, cx) = shown_pane(cx, &["a.md", "b.md"]).await;
    pane.update_in(cx, |pane, window, cx| {
        pane.start_entry(EntryKind::Rename, "a.md", window, cx)
    });
    commit(&pane, "b.md", cx);
    pane.read_with(cx, |pane, _| {
        let entry = pane.entry.as_ref().expect("the bar stays open");
        assert_eq!(
            entry.error.as_deref(),
            Some("Something is already called b.md here.")
        );
        assert!(!pane.action_running());
    });
    assert!(Path::new(&root).join("a.md").is_file());
}

#[gpui::test]
async fn delete_moves_to_trash_without_a_dialog(cx: &mut TestAppContext) {
    let name = format!("trash-me-{}.md", std::process::id());
    let (_backend, root, _session, pane, cx) = shown_pane(cx, &[name.as_str(), "keep.md"]).await;
    pane.update_in(cx, |pane, window, cx| {
        pane.delete_entry(name.clone(), window, cx)
    });
    done(&pane, cx).await;
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
    // Only this test's own file leaves the Trash it was moved to.
    if let Some(home) = std::env::var_os("HOME") {
        let trash = Path::new(&home).join(".local/share/Trash");
        let _ = fs::remove_file(trash.join("files").join(&name));
        let _ = fs::remove_file(trash.join("info").join(format!("{name}.trashinfo")));
    }
}

#[gpui::test]
async fn cut_then_paste_moves_and_clears_the_clipboard(cx: &mut TestAppContext) {
    let (_backend, root, _session, pane, cx) = shown_pane(cx, &["a.md", "dest/"]).await;
    pane.update(cx, |pane, cx| {
        pane.metadata_action("a.md".into(), "cut", cx)
    });
    done(&pane, cx).await;
    pane.read_with(cx, |pane, cx| {
        assert_eq!(pane.clipboard_chip("a.md", cx), Some("cut"))
    });
    pane.update_in(cx, |pane, window, cx| pane.paste_into("dest", window, cx));
    done(&pane, cx).await;
    assert!(Path::new(&root).join("dest/a.md").is_file());
    assert!(!Path::new(&root).join("a.md").exists());
    pane.read_with(cx, |pane, cx| {
        assert_eq!(pane.action_error, None);
        assert_eq!(pane.clipboard_chip("a.md", cx), None);
        assert_eq!(pane.selected.as_deref(), Some("dest/a.md"));
    });
    // The clipboard is empty: Paste says to copy first.
    pane.update_in(cx, |pane, window, cx| pane.paste_into("", window, cx));
    pane.read_with(cx, |pane, _| {
        assert_eq!(
            pane.action_error.as_deref(),
            Some("Copy or cut a file first.")
        )
    });
}

#[gpui::test]
async fn copy_then_paste_copies_a_folder(cx: &mut TestAppContext) {
    let (_backend, root, _session, pane, cx) =
        shown_pane(cx, &["docs/one.md", "docs/two.md", "dest/"]).await;
    pane.update(cx, |pane, cx| {
        pane.metadata_action("docs".into(), "copy", cx)
    });
    done(&pane, cx).await;
    pane.update_in(cx, |pane, window, cx| pane.paste_into("dest", window, cx));
    done(&pane, cx).await;
    for kept in [
        "docs/one.md",
        "docs/two.md",
        "dest/docs/one.md",
        "dest/docs/two.md",
    ] {
        assert!(Path::new(&root).join(kept).is_file(), "{kept}");
    }
    pane.read_with(cx, |pane, cx| {
        assert_eq!(pane.action_error, None);
        // A copy stays on the clipboard.
        assert_eq!(pane.clipboard_chip("docs", cx), Some("copy"));
    });
}

#[gpui::test]
async fn read_only_folder_disables_writes_with_the_reason(cx: &mut TestAppContext) {
    let (_backend, root, session, pane, cx) = shown_pane(cx, &["a.md"]).await;
    make_read_only(&session, cx);
    pane.update_in(cx, |pane, window, cx| {
        pane.start_entry(EntryKind::NewFile, "", window, cx);
        assert!(pane.entry.is_none());
        assert_eq!(
            pane.action_error.as_deref(),
            Some(super::panel_message::READ_ONLY)
        );
        pane.delete_entry("a.md".into(), window, cx);
        assert_eq!(
            pane.write_block(cx).as_deref(),
            Some(super::panel_message::READ_ONLY)
        );
    });
    done(&pane, cx).await;
    assert!(Path::new(&root).join("a.md").is_file());
}
