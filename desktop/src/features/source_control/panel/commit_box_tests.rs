use super::{amend_needs_confirm, can_commit, commit_hint, commit_label, stage_hint, stageable};
use crate::features::source_control::git_status::{ChangedFile, GitStatus};
fn file(path: &str, index: &str, worktree: &str) -> ChangedFile {
    ChangedFile {
        path: path.into(),
        original_path: None,
        index: index.into(),
        worktree: worktree.into(),
    }
}
fn status(files: Vec<ChangedFile>, staged: usize) -> GitStatus {
    GitStatus {
        branch: Some("lane/git".into()),
        head: Some("a".repeat(40)),
        ahead: 1,
        behind: 0,
        has_upstream: true,
        total_files: files.len(),
        files,
        total_staged: Some(staged),
        fingerprint: "f".into(),
        checkout_root: None,
    }
}
// Pins: g9#2
#[test]
fn commit_is_disabled_without_staged_files_or_a_message_and_while_busy() {
    assert!(!can_commit(Some(&status(vec![], 0)), "message", false, false));
    assert!(!can_commit(Some(&status(vec![], 1)), "   \n", false, false));
    assert!(!can_commit(Some(&status(vec![], 1)), "message", false, true));
    assert!(!can_commit(None, "message", false, false));
    assert!(can_commit(Some(&status(vec![], 1)), "message", false, false));
    assert_eq!(
        commit_hint(Some(&status(vec![], 0)), "message", false, false),
        "Stage something first — a commit takes what is staged."
    );
    assert_eq!(
        commit_hint(Some(&status(vec![], 1)), "", false, false),
        "Type a commit message first."
    );
}
#[test]
fn amend_with_an_empty_message_relabels_and_needs_nothing_staged_but_a_commit() {
    assert!(can_commit(Some(&status(vec![], 0)), "", true, false));
    assert_eq!(commit_label(true), "Amend");
    assert_eq!(commit_label(false), "Commit");
    let unborn = GitStatus {
        head: None,
        ..status(vec![], 0)
    };
    assert!(!can_commit(Some(&unborn), "", true, false));
}
#[test]
fn amend_of_a_pushed_commit_asks_first() {
    let pushed = GitStatus {
        ahead: 0,
        ..status(vec![], 0)
    };
    assert!(amend_needs_confirm(&pushed, true));
    assert!(!amend_needs_confirm(&pushed, false));
    assert!(!amend_needs_confirm(&status(vec![], 0), true));
}
// Pins: g9#1
#[test]
fn stage_all_takes_changes_and_untracked_files_but_never_marks_a_conflict_resolved() {
    let current = status(
        vec![
            file("changed", ".", "M"),
            file("new", "?", "?"),
            file("both", "M", "M"),
            file("staged", "M", "."),
            file("conflict", "U", "U"),
        ],
        2,
    );
    assert_eq!(stageable(&current), ["both", "changed", "new"]);
    assert_eq!(stage_hint(Some(&current), false), "Stage all 3 changed files");
    assert_eq!(
        stage_hint(Some(&status(vec![file("staged", "M", ".")], 1)), false),
        "Everything is already staged."
    );
}
