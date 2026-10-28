//! What the Source control tab makes of a status: the branch line (scm#9), the three lists and their letters
//! (scm#10), and what the cap leaves out (scm#11).

use serde_json::json;

use super::{GitStatus, Section};

fn status(files: serde_json::Value, total: usize) -> GitStatus {
    serde_json::from_value(json!({
        "branch": "lane/scm", "head": "0123456789abcdef", "ahead": 2, "behind": 1, "hasUpstream": true,
        "files": files, "totalFiles": total
    }))
    .unwrap()
}

fn file(path: &str, index: &str, worktree: &str) -> serde_json::Value {
    json!({ "path": path, "originalPath": null, "index": index, "worktree": worktree })
}

/// Each section's (letter, path) pairs.
fn listed(status: &GitStatus, section: Section) -> Vec<(char, String)> {
    status
        .section(section)
        .into_iter()
        .map(|(letter, file)| (letter, file.path.clone()))
        .collect()
}

// Pins: scm#9
#[test]
fn the_branch_line_says_the_branch_and_its_upstream_counts() {
    assert_eq!(status(json!([]), 0).branch_line(), "lane/scm ↑2 ↓1");
    let mut detached = status(json!([]), 0);
    detached.branch = None;
    detached.has_upstream = false;
    assert_eq!(detached.branch_line(), "detached 0123456, no upstream");
}

// Pins: scm#10
#[test]
fn files_go_in_staged_changes_and_untracked_sorted_with_their_letters() {
    let status = status(
        json!([
            file("src/b.rs", "M", "."),
            file("src/a.rs", "M", "M"),
            file("gone.txt", ".", "D"),
            file("new folder/x.txt", "?", "?"),
            file("both.rs", "U", "U"),
            file("added.rs", "A", "."),
        ]),
        6,
    );
    assert_eq!(
        listed(&status, Section::Staged),
        vec![
            ('A', "added.rs".to_string()),
            ('M', "src/a.rs".to_string()),
            ('M', "src/b.rs".to_string()),
        ]
    );
    assert_eq!(
        listed(&status, Section::Changes),
        vec![
            ('C', "both.rs".to_string()),
            ('D', "gone.txt".to_string()),
            ('M', "src/a.rs".to_string()),
        ]
    );
    assert_eq!(
        listed(&status, Section::Untracked),
        vec![('U', "new folder/x.txt".to_string())]
    );
}

// Pins: scm#10
#[test]
fn a_renamed_file_shows_its_old_path() {
    let renamed = status(
        json!([{ "path": "new.rs", "originalPath": "old.rs", "index": "R", "worktree": "." }]),
        1,
    );
    let (_, file) = renamed.section(Section::Staged)[0];
    assert_eq!(file.label(), "old.rs → new.rs");
}

// Pins: scm#11
#[test]
fn the_pane_says_how_many_files_the_cap_leaves_out() {
    assert_eq!(status(json!([file("a", "M", ".")]), 1).more_line(), None);
    assert_eq!(
        status(json!([file("a", "M", ".")]), 1201)
            .more_line()
            .as_deref(),
        Some("1200 more files aren't listed.")
    );
}
