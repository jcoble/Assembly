//! A file's staged and working parts, a branch patch cut per file, and which reads a round starts and keeps.
use super::{
    BranchFiles, ReadFile, Scope, Wanted, WorkingCopy, is_working_part, read_branch, read_file,
    wanted_files,
};
use crate::features::changes::{
    branch_diff::BranchDiff,
    file_diff::{CUT, FileDiff},
};
use crate::features::source_control::git_status::GitStatus;
use serde_json::json;

fn patch(path: &str, line: usize, old: &str, new: &str) -> String {
    format!(
        "diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -{line} +{line} @@\n-{old}\n+{new}\n"
    )
}
fn diff(path: &str, staged: &str, unstaged: &str) -> FileDiff {
    FileDiff {
        path: path.into(),
        staged: staged.into(),
        unstaged: unstaged.into(),
        binary: false,
        truncated: false,
    }
}
fn read(path: &str, version: &str) -> ReadFile {
    read_file(&diff(path, "", &patch(path, 1, "old", version))).unwrap()
}
fn wanted(paths: &[&str]) -> Vec<Wanted> {
    paths
        .iter()
        .map(|path| Wanted {
            path: (*path).into(),
            original_path: None,
        })
        .collect()
}
fn answered(working: &WorkingCopy) -> Vec<&str> {
    working.answers().keys().map(String::as_str).collect()
}

// Staged and working hunks can cover the same lines, which one parsed file refuses.
#[test]
fn overlapping_staged_and_working_lines_both_survive_under_one_path() {
    let file = read_file(&diff(
        "src/a.rs",
        &patch("src/a.rs", 5, "base", "staged"),
        &patch("src/a.rs", 5, "staged", "working"),
    ))
    .unwrap();
    assert!(file.both);
    assert_eq!(file.files.len(), 2);
    assert!(
        file.files
            .iter()
            .all(|part| part.path().as_ref() == "src/a.rs")
    );
    assert!(!is_working_part(&file.files[0]));
    assert!(is_working_part(&file.files[1]));
    assert_eq!(file.files[0].additions() + file.files[1].additions(), 2);
}
#[test]
fn a_line_of_the_file_cannot_pass_for_the_working_part_marker() {
    let forged = "diff --git a/a.rs b/a.rs\n--- a/a.rs\n+++ b/a.rs\n@@ -1 +1 @@\n-a\n+assembly-part: working tree\n";
    let file = read_file(&diff("a.rs", "", forged)).unwrap();
    assert!(!file.both);
    assert!(!is_working_part(&file.files[0]));
}
#[test]
fn one_sided_binary_and_combined_files_keep_what_they_have() {
    let working = read_file(&diff("a.rs", "", &patch("a.rs", 1, "a", "b"))).unwrap();
    assert!(!working.both);
    assert_eq!(working.files.len(), 1);
    assert!(working.notes.is_empty());

    let binary = read_file(&FileDiff {
        binary: true,
        ..diff("logo.png", "", "")
    })
    .unwrap();
    assert!(binary.files.is_empty());
    assert_eq!(
        binary.notes,
        ["Binary file, so there is no line-by-line comparison."]
    );

    let conflict = "diff --cc a.rs\nindex 1,2..3\n--- a/a.rs\n+++ b/a.rs\n@@@ -1,1 -1,1 +1,1 @@@\n- a\n -b\n++c\n";
    let combined = read_file(&diff("a.rs", "", conflict)).unwrap();
    assert!(combined.files.is_empty());
    assert_eq!(combined.combined[0].text, conflict);
    assert_eq!(combined.combined[0].label, "Working tree");
}
#[test]
fn only_the_last_branch_file_can_be_cut_and_a_bad_file_costs_only_itself() {
    let branch = BranchDiff {
        base: "origin/main".into(),
        diff: format!(
            "{}diff --git a/bad.rs b/bad.rs\n--- a/bad.rs\n+++ b/bad.rs\n@@ nonsense @@\n{}",
            patch("src/a.rs", 1, "a", "b"),
            patch("z.rs", 1, "a", "b"),
        ),
        truncated: true,
    };
    let read = read_branch(&branch);
    let paths: Vec<&str> = read.files.iter().map(|(path, _)| path.as_str()).collect();
    assert_eq!(paths, ["src/a.rs", "z.rs"]);
    assert_eq!(read.unreadable, ["bad.rs"]);
    assert!(read.files[0].1.notes.is_empty());
    assert_eq!(read.files[1].1.notes, [CUT]);
    assert!(
        read_branch(&BranchDiff {
            base: "main".into(),
            diff: String::new(),
            truncated: false
        })
        .files
        .is_empty()
    );
}
#[test]
fn branch_reads_only_the_untracked_files_it_cannot_cover() {
    let status: GitStatus = serde_json::from_value(json!({
        "branch":"main","head":"a","ahead":0,"behind":0,"hasUpstream":false,"totalFiles":2,"totalStaged":0,"fingerprint":"f",
        "files":[
            {"path":"new.rs","originalPath":null,"index":"?","worktree":"?"},
            {"path":"edit.rs","originalPath":null,"index":" ","worktree":"M"},
        ]
    }))
    .unwrap();
    let paths = |scope| -> Vec<String> {
        wanted_files(&status, scope)
            .into_iter()
            .map(|file| file.path)
            .collect()
    };
    assert_eq!(paths(Scope::Uncommitted), ["new.rs", "edit.rs"]);
    assert_eq!(paths(Scope::Branch), ["new.rs"]);
}
#[test]
fn four_reads_go_out_at_once_with_the_picked_file_first() {
    let mut working = WorkingCopy::default();
    working.begin(wanted(&["a", "b", "c", "d", "e", "f"]), false, Some("f"));
    let started: Vec<String> =
        std::iter::from_fn(|| working.next_read().map(|file| file.path)).collect();
    assert_eq!(started, ["f", "a", "b", "c"]);
    assert_eq!(working.pending(), 6);
    let round = working.round();
    assert!(working.answered(round, "a", Some(read("a", "new"))));
    assert_eq!(
        working.next_read().map(|file| file.path).as_deref(),
        Some("d")
    );
    assert!(working.next_read().is_none());
    working.focus_first("e");
    assert_eq!(working.pending(), 5);
}
#[test]
fn a_reply_counts_only_for_its_own_round_and_only_once() {
    let mut working = WorkingCopy::default();
    let old = working.begin(wanted(&["a"]), false, None);
    assert!(working.next_read().is_some());
    let new = working.begin(wanted(&["a"]), false, None);
    assert!(!working.answered(old, "a", Some(read("a", "stale"))));
    assert!(working.answers().is_empty());
    assert!(working.next_read().is_some());
    assert!(working.answered(new, "a", Some(read("a", "fresh"))));
    assert!(!working.answered(new, "a", Some(read("a", "again"))));
    working.stop();
    assert_eq!(answered(&working), ["a"]);
    assert_eq!(working.pending(), 0);
}
#[test]
fn a_new_round_keeps_what_is_still_changed_and_drops_the_rest() {
    let mut working = WorkingCopy::default();
    let round = working.begin(wanted(&["a", "b"]), false, None);
    while working.next_read().is_some() {}
    working.answered(round, "a", Some(read("a", "1")));
    working.answered(round, "b", None);
    assert_eq!(working.unreadable().collect::<Vec<_>>(), ["b"]);
    let round = working.begin(wanted(&["b"]), false, None);
    assert!(working.answers().is_empty());
    assert_eq!(working.unreadable().count(), 0);
    working.next_read();
    working.answered(round, "b", Some(read("b", "1")));
    working.begin(wanted(&["b", "c"]), false, None);
    assert_eq!(answered(&working), ["b"]);
}
#[test]
fn a_branch_answer_replaces_the_last_ones_and_keeps_this_rounds_untracked_reads() {
    let mut working = WorkingCopy::default();
    let round = working.begin(wanted(&["gone"]), false, None);
    working.next_read();
    working.answered(round, "gone", Some(read("gone", "1")));

    let round = working.begin(wanted(&["new"]), true, None);
    assert_eq!(answered(&working), ["gone"]);
    assert_eq!(working.pending(), 2);
    working.next_read();
    working.answered(round, "new", Some(read("new", "1")));
    assert!(!working.branch_answered(round - 1, None));
    let branch = BranchFiles {
        files: vec![
            ("kept".into(), read("kept", "1")),
            ("new".into(), read("new", "stale")),
        ],
        unreadable: vec!["bad".into()],
    };
    assert!(working.branch_answered(round, Some(branch)));
    assert_eq!(answered(&working), ["kept", "new"]);
    assert_eq!(working.unreadable().collect::<Vec<_>>(), ["bad"]);
    assert_eq!(working.pending(), 0);
    assert!(!working.branch_answered(round, None));
}
#[test]
fn the_stack_redraws_only_when_a_finished_round_changed_something() {
    let mut working = WorkingCopy::default();
    let round = working.begin(wanted(&["a", "b"]), false, None);
    assert!(!working.should_show());
    while working.next_read().is_some() {}
    working.answered(round, "a", Some(read("a", "1")));
    assert!(
        working.should_show(),
        "the first answer of a new selection shows at once"
    );
    assert!(working.shown_changed());
    assert!(!working.should_show(), "the next waits");
    working.answered(round, "b", Some(read("b", "1")));
    assert!(working.should_show());
    assert!(working.shown_changed());

    let round = working.begin(wanted(&["a", "b"]), false, None);
    while working.next_read().is_some() {}
    working.answered(round, "a", Some(read("a", "1")));
    assert!(
        !working.should_show(),
        "a refresh waits for its round to end"
    );
    working.answered(round, "b", Some(read("b", "1")));
    assert!(working.should_show());
    assert!(
        !working.shown_changed(),
        "the same content doesn't reset the scroll"
    );

    let round = working.begin(wanted(&["a", "b"]), false, None);
    while working.next_read().is_some() {}
    working.answered(round, "a", Some(read("a", "2")));
    working.answered(round, "b", Some(read("b", "1")));
    assert!(working.shown_changed());
}
