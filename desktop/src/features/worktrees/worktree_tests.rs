//! What a worktree's row says about it, and what the filter and the header make of a list.

use serde_json::json;

use super::{Tone, Worktree};
use crate::features::worktrees::worktree_list::WorktreeList;

fn worktree(path: &str, branch: Option<&str>) -> Worktree {
    serde_json::from_value(json!({
        "path": path, "branch": branch, "head": "0123456789abcdef0123456789abcdef01234567", "isMain": false,
        "isBare": false, "isDirty": false, "unpushed": 0, "locked": false, "prunable": false,
        "headSubject": "Add the reader", "headCommittedAt": 1_793_000_000_000i64
    }))
    .unwrap()
}

fn texts(worktree: &Worktree) -> Vec<(String, Tone)> {
    worktree
        .marks()
        .into_iter()
        .map(|mark| (mark.text, mark.tone))
        .collect()
}

// Pins: scm#1
#[test]
fn a_row_is_named_by_its_branch_or_as_detached_with_its_short_id() {
    assert_eq!(worktree("/r/a", Some("lane/scm")).name(), "lane/scm");
    assert_eq!(worktree("/r/b", None).name(), "detached 0123456");
}

// Pins: scm#2
#[test]
fn a_clean_pushed_worktree_shows_no_warning() {
    assert!(texts(&worktree("/r/a", Some("a"))).is_empty());
}

// Pins: scm#2
#[test]
fn a_row_says_what_would_be_lost_and_git_s_own_marks() {
    let mut risky = worktree("/r/a", Some("a"));
    risky.is_dirty = true;
    risky.unpushed = 2;
    risky.locked = true;
    assert_eq!(
        texts(&risky),
        vec![
            ("Uncommitted".to_string(), Tone::Warn),
            ("2 not pushed".to_string(), Tone::Warn),
            ("Locked".to_string(), Tone::Quiet),
        ]
    );
    let mut gone = worktree("/r/b", Some("b"));
    gone.prunable = true;
    assert_eq!(
        texts(&gone),
        vec![("Folder gone".to_string(), Tone::Danger)]
    );
}

// Pins: scm#3
#[test]
fn an_open_row_shows_the_head_commit() {
    let open = worktree("/r/a", Some("a"));
    assert_eq!(open.head_line().as_deref(), Some("0123456 Add the reader"));
    assert!(open.committed().is_some());
}

// Pins: scm#5
#[test]
fn the_filter_matches_branch_or_folder_and_says_what_it_hides() {
    let mut main = worktree("/home/me/Assembly", Some("main"));
    main.is_main = true;
    let list = WorktreeList {
        worktrees: vec![
            main,
            worktree("/home/me/worktrees/scm", Some("lane/scm")),
            worktree("/home/me/worktrees/terminal", Some("lane/terminal")),
        ],
    };
    assert_eq!(list.shown("").len(), 3);
    assert_eq!(list.hidden_line(""), None);
    let shown: Vec<String> = list.shown("SCM").iter().map(|w| w.name()).collect();
    assert_eq!(shown, vec!["lane/scm"]);
    assert_eq!(
        list.hidden_line("scm").as_deref(),
        Some("2 worktrees are hidden by the filter.")
    );
}
