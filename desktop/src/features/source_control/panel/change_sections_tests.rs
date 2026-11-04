use super::{RowWrite, change_word, file_reasons, row_write, stageable_in};
use crate::features::source_control::actions_menu::{READ_ONLY_SCOPE, STILL_RUNNING};
use crate::features::source_control::git_status::{ChangedFile, GitStatus, Section};

fn file(path: &str, index: &str, worktree: &str) -> ChangedFile {
    ChangedFile {
        path: path.into(),
        original_path: None,
        index: index.into(),
        worktree: worktree.into(),
    }
}
fn status(files: Vec<ChangedFile>) -> GitStatus {
    GitStatus {
        branch: Some("main".into()),
        head: Some("a".repeat(40)),
        ahead: 0,
        behind: 0,
        has_upstream: true,
        total_files: files.len(),
        files,
        total_staged: None,
        fingerprint: "f".into(),
        checkout_root: Some("/root".into()),
    }
}
// Pins: g9#8
#[test]
fn a_rows_first_button_stages_what_is_left_and_unstages_only_a_fully_staged_file() {
    assert_eq!(row_write(&file("a", ".", "M"), 'M'), RowWrite::Stage);
    assert_eq!(row_write(&file("a", "M", "M"), 'M'), RowWrite::Stage);
    assert_eq!(row_write(&file("a", "M", "."), 'M'), RowWrite::Unstage);
    assert_eq!(row_write(&file("a", "U", "U"), '!'), RowWrite::Resolve);
    assert_eq!(row_write(&file("a", "?", "?"), '?'), RowWrite::Stage);
}
#[test]
fn on_another_checkout_a_file_only_keeps_its_copy_path() {
    let reasons = file_reasons(&file("a", ".", "M"), true, false).read_only();
    for reason in [reasons.stage, reasons.unstage, reasons.discard, reasons.open] {
        assert_eq!(reason, Some(READ_ONLY_SCOPE));
    }
    assert_eq!(reasons.copy_path, None);
}
#[test]
fn a_blocked_file_action_says_why() {
    let reasons = file_reasons(&file("a", "M", "."), true, false);
    assert_eq!(
        reasons.stage,
        Some("Everything in this file is already staged.")
    );
    assert_eq!(reasons.unstage, None);
    let reasons = file_reasons(&file("a", ".", "M"), false, false);
    assert_eq!(reasons.unstage, Some("Nothing about this file is staged."));
    assert_eq!(
        reasons.copy_path,
        Some("This panel is not pointed at a repository folder yet.")
    );
    let reasons = file_reasons(&file("a", ".", "D"), true, false);
    assert_eq!(
        reasons.open,
        Some("This file was deleted, so there is nothing on disk to open.")
    );
    let reasons = file_reasons(&file("a", ".", "M"), true, true);
    assert_eq!(reasons.stage, Some(STILL_RUNNING));
    assert_eq!(reasons.discard, Some(STILL_RUNNING));
    assert_eq!(reasons.open, None);
}
#[test]
fn a_file_with_no_folder_to_open_says_so_before_it_says_it_was_deleted() {
    let reasons = file_reasons(&file("a", ".", "D"), false, false);
    assert_eq!(
        reasons.open,
        Some("This panel is not pointed at a repository folder yet.")
    );
}
#[test]
fn only_a_file_changed_two_ways_gets_a_change_word() {
    assert_eq!(change_word(&file("a", "M", "D")).as_deref(), Some("M + D"));
    assert_eq!(change_word(&file("a", "M", "M")), None);
    assert_eq!(change_word(&file("a", ".", "M")), None);
    assert_eq!(change_word(&file("a", "?", "?")), None);
}
#[test]
fn stage_all_leaves_conflicts_for_a_person() {
    let status = status(vec![
        file("clean", ".", "M"),
        file("conflict", "U", "U"),
        file("new", "?", "?"),
    ]);
    assert_eq!(
        stageable_in(&status, Section::Changes),
        vec!["clean".to_string()]
    );
    assert_eq!(
        stageable_in(&status, Section::Untracked),
        vec!["new".to_string()]
    );
}
